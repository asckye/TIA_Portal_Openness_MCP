#if TIA_APPROVAL_PRIVATE_JOURNAL
using VaultPayload = TiaOpenness.Shared.ApprovalJournalPayload;
#else
using VaultPayload = TiaOpenness.Shared.CallJournalPayload;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TiaOpenness.Shared
{
    // Instances hold only an encrypted snapshot. A dispatch creates its own instance; no process-wide cache is used.
    internal sealed class SecretVault
    {
        internal static string VaultPath => Path.Combine(DataLocations.Current.ConfigDirectory, "secrets.vault");
        private readonly string path;
        internal SecretVault(string? path = null) => this.path = path ?? VaultPath;

        internal static void ValidateName(string name)
        {
            if (name == null || !VaultPayload.IsVaultReference("vault:" + name))
                throw new ArgumentException("Vault names require 1–64 ASCII letters, digits, underscores, dots or hyphens, starting with a letter or digit.",
                    nameof(name));
        }

        internal string[] Names(bool validateProtected = true)
            => (validateProtected ? Read() : ReadUnlocked(validateProtected: false)).Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        // Admission validates DPAPI and UTF-8 without constructing the credential string.
        internal bool ContainsName(string name)
        {
            ValidateName(name);
            return Read().ContainsKey(name);
        }

        internal bool TryResolve(string name, out string value)
        {
            ValidateName(name);
            value = "";
            if (!Read().TryGetValue(name, out var encrypted)) return false;
            try
            {
                byte[] bytes = CurrentUserProtection.Transform(Convert.FromBase64String(encrypted), false);
                try { value = new UTF8Encoding(false, true).GetString(bytes); }
                finally { Array.Clear(bytes, 0, bytes.Length); }
                return true;
            }
            catch (Exception error) when (error is CryptographicException || error is FormatException || error is DecoderFallbackException
                || error is PlatformNotSupportedException)
            { throw Unreadable(); }
        }

        internal void Set(string name, string value, AuditLog? audit = null)
        {
            ValidateName(name);
            if (value == null) throw new ArgumentNullException(nameof(value));
            Change(name, Convert.ToBase64String(CurrentUserProtection.Transform(Encoding.UTF8.GetBytes(value), true)), audit);
        }

        internal bool Remove(string name, AuditLog? audit = null)
        {
            ValidateName(name);
            return Change(name, null, audit);
        }

        private bool Change(string name, string? encrypted, AuditLog? audit)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (JournalFileLock.Acquire(path + ".lock"))
            {
                var entries = ReadUnlocked();
                if (encrypted == null && !entries.Remove(name)) return false;
                if (encrypted != null) entries[name] = encrypted;
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(JsonSerializer.Serialize(new { version = 1, secrets = entries }));
                        writer.Flush();
                        stream.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            (audit ?? AuditLog.Current).Append("secret-vault-change", "", "workbench", "", "vault:" + name,
                outcome: encrypted == null ? "remove" : "set", actor: ActorScope.Workbench);
            return true;
        }

        private Dictionary<string, string> Read(bool validateProtected = true)
        {
            try
            {
                using var held = JournalFileLock.Acquire(path + ".lock", readOnly: true);
                return ReadUnlocked(validateProtected);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException)
            { throw Unreadable(); }
        }

        private Dictionary<string, string> ReadUnlocked(bool validateProtected = true)
        {
            string text;
            try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
            catch (FileNotFoundException) /* swallow(probe-optional): an absent vault contains no entries. */
            { return new Dictionary<string, string>(StringComparer.Ordinal); }
            catch (DirectoryNotFoundException) /* swallow(probe-optional): an absent configuration directory contains no vault. */
            { return new Dictionary<string, string>(StringComparer.Ordinal); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException
                || error is DecoderFallbackException)
            { throw Unreadable(); }
            try
            {
                using var json = JsonDocument.Parse(text);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                    || root.GetProperty("version").GetInt32() != 1 || root.GetProperty("secrets").ValueKind != JsonValueKind.Object)
                    throw Unreadable();
                var entries = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in root.GetProperty("secrets").EnumerateObject())
                {
                    ValidateName(entry.Name);
                    string value = entry.Value.GetString() ?? throw Unreadable();
                    byte[] encrypted = Convert.FromBase64String(value);
                    if (encrypted.Length == 0 || entries.ContainsKey(entry.Name)) throw Unreadable();
                    if (validateProtected)
                    {
                        byte[] plaintext = CurrentUserProtection.Transform(encrypted, false);
                        try { _ = new UTF8Encoding(false, true).GetCharCount(plaintext); }
                        finally { Array.Clear(plaintext, 0, plaintext.Length); }
                    }
                    entries.Add(entry.Name, value);
                }
                return entries;
            }
            catch (Exception error) when (error is JsonException || error is KeyNotFoundException || error is InvalidOperationException
                || error is ArgumentException || error is FormatException || error is OverflowException
                || error is CryptographicException || error is PlatformNotSupportedException)
            { throw Unreadable(); }
        }

        private static IOException Unreadable() => new IOException("secret-vault-unreadable: the current user cannot read the secret vault.");
    }
}
