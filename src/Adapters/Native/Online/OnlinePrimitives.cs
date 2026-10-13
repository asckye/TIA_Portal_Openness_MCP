using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW.Blocks;
#if ONLINE_BLOCK_INTERFACE
using Siemens.Engineering.SW.Blocks.Interface;
#endif
#if ONLINE_FINGERPRINTS
using Siemens.Engineering.FingerprintData;
using Siemens.Engineering.Online.Configurations;
#endif

namespace TiaMcp.Adapters.Native.Online;

internal static class OnlinePrimitives
{
#if ONLINE_BLOCK_INTERFACE
        public static PlcBlockInterface Interface(DataBlock value) => value.Interface;
#endif
#if ONLINE_DOWNLOAD
        public static OnlineState? StateOrNull(OnlineProvider value) => value?.State;
#endif
#if ONLINE_DOWNLOAD
        public static InterfaceSnapshot Snapshot(IEngineeringServiceProvider value) => value.GetService<InterfaceSnapshot>();
#endif
#if ONLINE_DOWNLOAD
        public static InterfaceSnapshot SnapshotOrNull(IEngineeringServiceProvider value) => value == null ? null : Snapshot(value);
#endif
#if ONLINE_DOWNLOAD
        public static void Export(InterfaceSnapshot value, FileInfo file, ExportOptions options) => value.Export(file, options);
#endif
#if ONLINE_FINGERPRINTS
        public static ConnectionConfiguration Configuration(FingerprintDataProvider value) => value.Configuration;
#endif
#if ONLINE_FINGERPRINTS
        public static string Address(ConfigurationAddress value) => value.Address;
#endif
#if ONLINE_FINGERPRINTS
        public static OnlineConfigurationDelegate Handler(Action<OnlineConfiguration> handler) => new OnlineConfigurationDelegate(handler);
#endif
#if ONLINE_FINGERPRINTS
        public static FingerprintDataResult Fingerprints(FingerprintDataProvider value, ConfigurationAddress address, OnlineConfigurationDelegate handler) => value.GetFingerprintData(address, handler);
#endif
#if ONLINE_FINGERPRINTS
        public static FingerprintDataItemComposition Items(FingerprintDataResult value) => value.FingerprintDataItems;
#endif
#if ONLINE_FINGERPRINTS
        public static string Identifier(FingerprintDataItem value) => value.FingerprintDataIdentifier;
#endif
#if ONLINE_FINGERPRINTS
        public static string Value(FingerprintDataItem value) => value.FingerprintDataValue;
#endif
#if ONLINE_FINGERPRINTS
        public static void Password(OnlinePasswordConfiguration value, SecureString password) => value.SetPassword(password);
#endif
#if ONLINE_TLS
        public static TlsVerificationConfigurationSelection Selection(TlsVerificationConfiguration value) => value.CurrentSelection;
#endif
#if ONLINE_TLS
        public static void Selection(TlsVerificationConfiguration value, TlsVerificationConfigurationSelection selection) => value.CurrentSelection = selection;
#endif
#if ONLINE_TLS
        public static string PlcName(TlsVerificationConfiguration value) => value.PlcName;
#endif
#if ONLINE_TLS
        public static string VerificationInfo(TlsVerificationConfiguration value) => value.VerificationInfo;
#endif
}
