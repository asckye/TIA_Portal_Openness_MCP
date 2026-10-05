using System.Collections.Generic;

namespace TiaMcpConfigurator;

// Explicit view-only input for the offline render harness; production never supplies it.
internal sealed record ConfigurationPreview(
    string ReleaseKey, string InstallPath, string Address, bool Local, bool Running,
    bool Detected, string Secret, string Log, List<ClientProfile> Clients);
