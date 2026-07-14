using System.Text.Json;
using MobileData.Core;
using Windows.Networking.Connectivity;

namespace MobileData.Providers;

/// <summary>
/// Current internet connection as seen by Windows.
/// </summary>
/// <param name="IsConnected">There is an active internet connection.</param>
/// <param name="NetworkName">Wi-Fi SSID, or the connection profile name.</param>
/// <param name="IsMetered">Windows considers the connection metered (cost != unrestricted).</param>
/// <param name="IsCellular">The profile is a built-in WWAN/cellular adapter.</param>
/// <param name="IsTethered">Best-effort: connected via a metered Wi-Fi network or cellular,
/// which is what a phone hotspot looks like to a PC.</param>
public record NetworkStatus(
    bool IsConnected,
    string? NetworkName,
    bool IsMetered,
    bool IsCellular,
    bool IsTethered);

/// <summary>
/// Inspects the active network connection. A phone hotspot can't tell the PC which
/// carrier it is, so we can only report the network name and whether it looks like a
/// tethered/metered connection — the carrier itself is chosen by which widget is added
/// (or mapped from the SSID via configuration later).
/// </summary>
public static class NetworkDetector
{
    /// <summary>Full path of the SSID map: <c>%USERPROFILE%\.mobile-widget\networks.json</c>.</summary>
    public static string NetworksConfigPath =>
        Path.Combine(UsageCache.Directory, "networks.json");

    public static NetworkStatus Detect()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null ||
                profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
            {
                return new NetworkStatus(false, null, false, false, false);
            }

            var isCellular = profile.IsWwanConnectionProfile;

            string? name = null;
            if (profile.IsWlanConnectionProfile)
                name = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
            if (string.IsNullOrWhiteSpace(name))
                name = profile.ProfileName;

            var cost = profile.GetConnectionCost();
            var isMetered = cost is not null &&
                            cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable;

            // A phone hotspot appears either as a cellular profile or as a metered Wi-Fi network.
            // Windows does not auto-mark a hotspot metered, so an SSID the user has declared in
            // networks.json also counts — that declaration is the only dependable signal, since the
            // carrier itself is invisible from the PC (Fido and Rogers share Rogers' AS812).
            var isTethered = isCellular || isMetered || IsKnownHotspot(name);

            return new NetworkStatus(true, name, isMetered, isCellular, isTethered);
        }
        catch
        {
            return new NetworkStatus(false, null, false, false, false);
        }
    }

    /// <summary>
    /// True when the SSID is listed in networks.json, which maps a hotspot SSID to the carrier
    /// that phone is on, e.g. <c>{ "K's iPhone": "fido" }</c>. Missing or malformed file: no
    /// known hotspots.
    /// </summary>
    private static bool IsKnownHotspot(string? ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid)) return false;

        try
        {
            if (!File.Exists(NetworksConfigPath)) return false;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(NetworksConfigPath));
            return map is not null && map.ContainsKey(ssid);
        }
        catch
        {
            return false;
        }
    }
}
