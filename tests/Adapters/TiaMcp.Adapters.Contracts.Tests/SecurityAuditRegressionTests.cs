using System.Reflection;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests
{
    public sealed class SecurityAuditRegressionTests
    {
        [Fact]
        public void Unverified_security_attribute_lists_refuse_all_candidate_names()
        {
            Assert.Empty(SecurityAttributePolicy.PutGetNames);
            Assert.Empty(SecurityAttributePolicy.CertificateNames);
            Assert.Empty(SecurityAttributePolicy.SyslogNames);
            Assert.Throws<NotSupportedException>(() => SecurityAttributePolicy.SelectExact(new[] { "PermitPutGet" }, SecurityAttributePolicy.PutGetNames));
            Assert.Throws<NotSupportedException>(() => SecurityAttributePolicy.RequireApproved("WebserverCertificate", SecurityAttributePolicy.CertificateNames));
            Assert.Throws<NotSupportedException>(() => SecurityAttributePolicy.RequireApproved("SysLogTrustedCertificateIds", SecurityAttributePolicy.SyslogNames));
        }

        [Fact]
        public void Exact_attribute_selection_refuses_fuzzy_names_and_ambiguous_owners()
        {
            var approved = new[] { "ConfirmedName" };
            Assert.Equal(1, SecurityAttributePolicy.SelectExact(new[] { "Other", "ConfirmedName" }, approved));
            Assert.Throws<NotSupportedException>(() => SecurityAttributePolicy.SelectExact(new[] { "confirmedname", "PrefixConfirmedName" }, approved));
            Assert.Throws<InvalidOperationException>(() => SecurityAttributePolicy.SelectExact(new[] { "ConfirmedName", "ConfirmedName" }, approved));
            Assert.Throws<InvalidOperationException>(() => SecurityAttributePolicy.SelectExact(new[] { "ConfirmedName", "Other" }, new[] { "ConfirmedName", "Other" }));
        }

        [Theory]
        [InlineData("ReadWrite", true, false)]
        [InlineData("Read", true, true)]
        [InlineData("Write", true, true)]
        [InlineData("ReadWrite", "True", true)]
        [InlineData("ReadWrite", 1, true)]
        [InlineData("ReadWrite", null, true)]
        public void Put_get_requires_readwrite_and_a_boolean(string mode, object? value, bool refuses)
        {
            if (refuses) Assert.Throws<NotSupportedException>(() => SecurityAttributePolicy.RequireBooleanWrite(mode, value));
            else SecurityAttributePolicy.RequireBooleanWrite(mode, value);
        }

        [Theory]
        [InlineData("17", "ProtectAllConfiguration")]
        [InlineData("18", " UNPROTECTALLCONFIGURATION ")]
        [InlineData("19", "protectALLConfiguration")]
        [InlineData("20", "UnprotectAllConfiguration")]
        [InlineData("16", "ProtectMasterSecret")]
        public void Release_gate_uses_normalized_protection_action(string release, string action)
        {
            Assert.NotNull(SecurityCapabilities.Unsupported(release, "ManagePlcProtection", action));
            Assert.Null(SecurityCapabilities.Unsupported("21", "ManagePlcProtection", action));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Password_policy_refusal_is_known_unchanged_even_through_reflection(bool reflected)
        {
            var meta = new Dictionary<string, object?>();
            int calls = 0;
            bool success = EngineeringCredentialRules.TrySetPassword(() => {
                calls++;
                var error = new Siemens.Engineering.EngineeringPasswordPolicyViolationException(new InvalidOperationException());
                if (reflected) throw new TargetInvocationException(error);
                throw error;
            }, meta);
            Assert.False(success);
            Assert.Equal(1, calls);
            Assert.Equal(false, meta["mayHaveChanged"]);
            Assert.Equal(false, meta["operationSuccess"]);
            Assert.Equal("password-policy", meta["precondition"]);
            Assert.False(new SecurityResult { Meta = meta }.MayHaveChanged);
            Assert.DoesNotContain("secret", string.Join(" ", meta.Values), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Other_password_failures_keep_uncertain_write_evidence()
        {
            var meta = new Dictionary<string, object?>();
            Assert.Throws<InvalidOperationException>(() => EngineeringCredentialRules.TrySetPassword(() => throw new InvalidOperationException(), meta));
            Assert.Equal(true, meta["mayHaveChanged"]);
            Assert.False(meta.ContainsKey("refusal"));
        }

        [Fact]
        public void Authentication_scope_keeps_password_alive_during_callback_and_refuses_late_callbacks()
        {
            var scope = new EngineeringCredentialRules.AuthenticationScope("fixture");
            scope.Use(password => { Assert.True(password.IsReadOnly()); Assert.Equal(7, password.Length); });
            scope.Dispose();
            int calls = 0;
            Assert.Throws<InvalidOperationException>(() => scope.Use(_ => calls++));
            Assert.Equal(0, calls);
            scope.Dispose();
        }
    }
}

namespace Siemens.Engineering
{
    // The production policy identifies the exact documented native exception, including reflection wrappers.
    public sealed class EngineeringPasswordPolicyViolationException : Exception
    {
        public EngineeringPasswordPolicyViolationException(Exception inner) : base("Synthetic policy refusal", inner) { }
    }
}
