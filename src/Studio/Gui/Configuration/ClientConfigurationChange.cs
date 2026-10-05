using System;
using System.IO;
using System.Linq;
using TiaOpenness.Gui.Localization;

namespace TiaMcpConfigurator
{
    public sealed class ClientConfigurationChange
    {
        private readonly byte[] original;
        private readonly string text;
        public string Path { get; }
        public bool RequiresMigration { get; }
        public string TargetCommand { get; }
        public string BackupPath { get; private set; }

        internal ClientConfigurationChange(string path, byte[] original, string text, bool migration, string command)
        { Path = path; this.original = original; this.text = text; RequiresMigration = migration; TargetCommand = command; }

        public void Apply(bool confirmed)
        { Apply(confirmed, (path, value) => ConfigCore.AtomicText(path, value, null)); }

        internal void Apply(bool confirmed, Action<string, string> write)
        {
            if (original != null && !confirmed) throw new InvalidOperationException(Loc.Current["Config.MigrationConfirmationRequired"]);
            if (!string.IsNullOrEmpty(TargetCommand) && !File.Exists(TargetCommand))
                throw new FileNotFoundException(Loc.Current["Config.TargetEngineUnavailable"] + " " + TargetCommand, TargetCommand);
            if (File.Exists(Path) != (original != null) || (original != null && !File.ReadAllBytes(Path).SequenceEqual(original)))
                throw new IOException(Loc.Current["Config.ClientChangedBeforeWrite"]);
            if (original != null)
            {
                BackupPath = Path + ".bak_" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture);
                using (var backup = new FileStream(BackupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    backup.Write(original, 0, original.Length);
            }
            try { write(Path, text); }
            catch (Exception failure)
            {
                try
                {
                    if (original == null) { if (File.Exists(Path)) File.Delete(Path); }
                    else if (!File.Exists(Path) || !File.ReadAllBytes(Path).SequenceEqual(original)) File.Copy(BackupPath, Path, true);
                }
                catch (Exception rollback)
                {
                    throw new IOException(Loc.Current.T("Config.ClientRollbackFailed", BackupPath), new AggregateException(failure, rollback));
                }
                throw;
            }
        }
    }
}
