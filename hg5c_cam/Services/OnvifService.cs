using hg5c_cam.Models;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace hg5c_cam.Services;

public class OnvifService
{
    private static readonly UriKind AbsoluteUri = UriKind.Absolute;
    private static readonly TimeSpan SoapRequestTimeout = TimeSpan.FromSeconds(6);
    private const double DefaultPanTiltMinStep = 0.001;
    private const double DefaultZoomMinStep = 0.01;
    private readonly object _ptzContextSync = new();
    private PtzContext? _cachedPtzContext;

    private sealed class PtzContext
    {
        public required string CacheKey { get; init; }
        public required Uri MediaServiceUri { get; init; }
        public required string ProfileToken { get; init; }
        public required NetworkCredential? Credentials { get; init; }
        public required List<Uri> PtzCandidates { get; init; }
    }

    private static string T(string key) => LocalizationService.TranslateCurrent(key);
    private static string TF(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    public async Task<IReadOnlyList<OnvifPreset>> GetPresetsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.UseOnvif)
        {
            throw new InvalidOperationException(T("ExceptionOnvifDisabled"));
        }

        var context = await ResolvePtzContextAsync(settings, cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;
        foreach (var ptzCandidate in context.PtzCandidates)
        {
            try
            {
                var presets = await ResolvePresetsFromPtzAsync(ptzCandidate, context.ProfileToken, context.Credentials, cancellationToken).ConfigureAwait(false);
                if (presets.Count == 0)
                {
                    continue;
                }

                return presets;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw new InvalidOperationException($"{T("ExceptionOnvifResolvePresetsFailed")} {lastError.Message}", lastError);
        }

        throw new InvalidOperationException(T("ExceptionOnvifResolvePresetsFailed"));
    }

    public async Task GotoPresetAsync(AppSettings settings, string presetToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(presetToken))
        {
            throw new ArgumentException(T("ExceptionOnvifPresetTokenRequired"), nameof(presetToken));
        }

        if (!settings.UseOnvif)
        {
            throw new InvalidOperationException(T("ExceptionOnvifDisabled"));
        }

        var context = await ResolvePtzContextAsync(settings, cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;
        foreach (var ptzCandidate in context.PtzCandidates)
        {
            try
            {
                await SendGotoPresetAsync(ptzCandidate, context.ProfileToken, presetToken, context.Credentials, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw new InvalidOperationException($"{T("ExceptionOnvifMoveToPresetFailed")} {lastError.Message}", lastError);
        }

        throw new InvalidOperationException(T("ExceptionOnvifMoveToPresetFailed"));
    }

    public async Task MoveRelativeAsync(AppSettings settings, double panDelta, double tiltDelta, CancellationToken cancellationToken = default)
    {
        if (!settings.UseOnvif)
        {
            throw new InvalidOperationException(T("ExceptionOnvifDisabled"));
        }

        var context = await ResolvePtzContextAsync(settings, cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;

        foreach (var ptzCandidate in context.PtzCandidates)
        {
            try
            {
                await SendRelativeMoveAsync(ptzCandidate, context.ProfileToken, panDelta, tiltDelta, context.Credentials, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw new InvalidOperationException($"{T("ExceptionOnvifMoveRelativeFailed")} {lastError.Message}", lastError);
        }

        throw new InvalidOperationException(T("ExceptionOnvifMoveRelativeFailed"));
    }

    public async Task<OnvifPtzCapabilities> GetPtzCapabilitiesAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.UseOnvif)
        {
            throw new InvalidOperationException(T("ExceptionOnvifDisabled"));
        }

        var context = await ResolvePtzContextAsync(settings, cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;

        foreach (var ptzCandidate in context.PtzCandidates)
        {
            try
            {
                return await ResolvePtzCapabilitiesAsync(
                    ptzCandidate,
                    context.MediaServiceUri,
                    context.ProfileToken,
                    context.Credentials,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw new InvalidOperationException(lastError.Message, lastError);
        }

        return new OnvifPtzCapabilities();
    }

    public Task ZoomInAsync(AppSettings settings, double zoomDelta = 0.1, CancellationToken cancellationToken = default)
    {
        var normalizedDelta = Math.Abs(zoomDelta);
        return MoveZoomRelativeAsync(settings, normalizedDelta, cancellationToken);
    }

    public Task ZoomOutAsync(AppSettings settings, double zoomDelta = 0.1, CancellationToken cancellationToken = default)
    {
        var normalizedDelta = Math.Abs(zoomDelta);
        return MoveZoomRelativeAsync(settings, -normalizedDelta, cancellationToken);
    }

    public async Task StopMoveAsync(AppSettings settings, bool stopPanTilt = true, bool stopZoom = true, CancellationToken cancellationToken = default)
    {
        if (!settings.UseOnvif)
        {
            throw new InvalidOperationException(T("ExceptionOnvifDisabled"));
        }

        var context = await ResolvePtzContextAsync(settings, cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;

        foreach (var ptzCandidate in context.PtzCandidates)
        {
            try
            {
                await SendStopAsync(ptzCandidate, context.ProfileToken, stopPanTilt, stopZoom, context.Credentials, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw new InvalidOperationException($"{T("ExceptionOnvifMoveRelativeFailed")} {lastError.Message}", lastError);
        }

        throw new InvalidOperationException(T("ExceptionOnvifMoveRelativeFailed"));
    }

    private async Task MoveZoomRelativeAsync(AppSettings settings, double zoomDelta, CancellationToken cancellationToken)
    {
        if (!settings.UseOnvif)
        {
            throw new InvalidOperationException(T("ExceptionOnvifDisabled"));
        }

        var context = await ResolvePtzContextAsync(settings, cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;

        foreach (var ptzCandidate in context.PtzCandidates)
        {
            try
            {
                await SendRelativeZoomAsync(ptzCandidate, context.ProfileToken, zoomDelta, context.Credentials, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw new InvalidOperationException($"{T("ExceptionOnvifZoomRelativeFailed")} {lastError.Message}", lastError);
        }

        throw new InvalidOperationException(T("ExceptionOnvifZoomRelativeFailed"));
    }

    public async Task<OnvifStreamInfo> ResolveRtspStreamAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var endpoint = GetRequiredOnvifDeviceServiceUri(settings);
        var credentials = ResolveCredentials(settings);
        try
        {
            var mediaServiceUrl = await ResolveMediaServiceUrlAsync(endpoint, credentials, cancellationToken).ConfigureAwait(false);
            var mediaServiceUri = BuildServiceUriUsingConfiguredAuthority(endpoint, mediaServiceUrl, "Media");
            var profileToken = await ResolveProfileTokenAsync(mediaServiceUri, settings.OnvifProfileToken, credentials, cancellationToken).ConfigureAwait(false);
            var (streamWidth, streamHeight) = await ResolveProfileResolutionAsync(mediaServiceUri, profileToken, credentials, cancellationToken).ConfigureAwait(false);
            var rtspUri = await ResolveStreamUriAsync(mediaServiceUri, profileToken, credentials, cancellationToken).ConfigureAwait(false);

            if (!Uri.TryCreate(rtspUri, AbsoluteUri, out var parsedRtspUri) || !string.Equals(parsedRtspUri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(T("ExceptionOnvifInvalidRtspUriInResponse"));
            }

            var rtspPort = parsedRtspUri.IsDefaultPort ? 554 : parsedRtspUri.Port;
            return new OnvifStreamInfo
            {
                DeviceServiceUrl = endpoint.AbsoluteUri,
                MediaServiceUrl = mediaServiceUri.AbsoluteUri,
                RtspUri = parsedRtspUri.AbsoluteUri,
                RtspPort = rtspPort,
                ProfileToken = profileToken,
                StreamWidth = streamWidth,
                StreamHeight = streamHeight
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(TF("ExceptionOnvifResolveRtspFailedWithError", ex.Message), ex);
        }
    }

    public async Task<IReadOnlyList<string>> GetProfileTokensAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var endpoint = GetRequiredOnvifDeviceServiceUri(settings);
        var credentials = ResolveCredentials(settings);

        try
        {
            var mediaServiceUrl = await ResolveMediaServiceUrlAsync(endpoint, credentials, cancellationToken).ConfigureAwait(false);
            var mediaServiceUri = BuildServiceUriUsingConfiguredAuthority(endpoint, mediaServiceUrl, "Media");
            var profiles = await TryResolveProfilesAsync(mediaServiceUri, credentials, cancellationToken).ConfigureAwait(false);

            return profiles
                .OrderByDescending(x => x.HasPtzConfiguration)
                .Select(x => x.Token)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(TF("ExceptionOnvifResolveProfilesFailedWithError", ex.Message), ex);
        }
    }

    private static Uri GetRequiredOnvifDeviceServiceUri(AppSettings settings)
    {
        if (Uri.TryCreate(settings.OnvifDeviceServiceUrl, AbsoluteUri, out var configuredOnvifUri) &&
            (string.Equals(configuredOnvifUri.Scheme, "http", StringComparison.OrdinalIgnoreCase) || string.Equals(configuredOnvifUri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
        {
            return configuredOnvifUri;
        }

        throw new InvalidOperationException(T("ExceptionOnvifNoValidEndpoint"));
    }

    private static NetworkCredential? ResolveCredentials(AppSettings settings)
    {
        var username = settings.Username;
        var password = settings.Password;

        if (string.IsNullOrWhiteSpace(username) &&
            Uri.TryCreate(settings.OnvifDeviceServiceUrl, AbsoluteUri, out var onvifUri) &&
            !string.IsNullOrWhiteSpace(onvifUri.UserInfo) &&
            onvifUri.UserInfo.Contains(':', StringComparison.Ordinal))
        {
            var parts = onvifUri.UserInfo.Split(':', 2);
            username = Uri.UnescapeDataString(parts[0]);
            password = Uri.UnescapeDataString(parts[1]);
        }

        if (string.IsNullOrWhiteSpace(username) &&
            Uri.TryCreate(settings.Url, AbsoluteUri, out var streamUri) &&
            !string.IsNullOrWhiteSpace(streamUri.UserInfo) &&
            streamUri.UserInfo.Contains(':', StringComparison.Ordinal))
        {
            var parts = streamUri.UserInfo.Split(':', 2);
            username = Uri.UnescapeDataString(parts[0]);
            password = Uri.UnescapeDataString(parts[1]);
        }

        return string.IsNullOrWhiteSpace(username) ? null : new NetworkCredential(username, password ?? string.Empty);
    }

    private static Uri BuildServiceUriUsingConfiguredAuthority(Uri deviceServiceUri, string discoveredServiceUrl, string serviceKind)
    {
        if (!Uri.TryCreate(discoveredServiceUrl, AbsoluteUri, out var discoveredUri))
        {
            throw new InvalidOperationException(TF("ExceptionOnvifServiceUrlInvalid", serviceKind));
        }

        var uriBuilder = new UriBuilder(deviceServiceUri)
        {
            Path = discoveredUri.AbsolutePath,
            Query = discoveredUri.Query
        };

        return uriBuilder.Uri;
    }

    private static async Task<string> ResolveMediaServiceUrlAsync(Uri deviceServiceUri, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        var document = await ResolveCapabilitiesDocumentAsync(deviceServiceUri, credentials, cancellationToken, "Media").ConfigureAwait(false);
        var mediaNode = document.Descendants().FirstOrDefault(x => string.Equals(x.Name.LocalName, "Media", StringComparison.OrdinalIgnoreCase));
        var media2Node = document.Descendants().FirstOrDefault(x => string.Equals(x.Name.LocalName, "Media2", StringComparison.OrdinalIgnoreCase));

        var xAddr = mediaNode?.Descendants().FirstOrDefault(x => string.Equals(x.Name.LocalName, "XAddr", StringComparison.OrdinalIgnoreCase))?.Value
                    ?? media2Node?.Descendants().FirstOrDefault(x => string.Equals(x.Name.LocalName, "XAddr", StringComparison.OrdinalIgnoreCase))?.Value;

        if (string.IsNullOrWhiteSpace(xAddr))
        {
            throw new InvalidOperationException(T("ExceptionOnvifMediaUrlMissing"));
        }

        return xAddr.Trim();
    }

    private static async Task<string> ResolvePtzServiceUrlAsync(Uri deviceServiceUri, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        var document = await ResolveCapabilitiesDocumentAsync(deviceServiceUri, credentials, cancellationToken, "PTZ").ConfigureAwait(false);
        var ptzNode = document.Descendants().FirstOrDefault(x => string.Equals(x.Name.LocalName, "PTZ", StringComparison.OrdinalIgnoreCase));
        var xAddr = ptzNode?.Descendants().FirstOrDefault(x => string.Equals(x.Name.LocalName, "XAddr", StringComparison.OrdinalIgnoreCase))?.Value;

        if (string.IsNullOrWhiteSpace(xAddr))
        {
            throw new InvalidOperationException(T("ExceptionOnvifPtzUrlMissing"));
        }

        return xAddr.Trim();
    }

    private static async Task<string> ResolveProfileTokenAsync(Uri mediaServiceUri, string preferredToken, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(preferredToken))
        {
            return preferredToken;
        }

        var profiles = await TryResolveProfilesAsync(mediaServiceUri, credentials, cancellationToken).ConfigureAwait(false);
        var tokens = profiles.Select(x => x.Token).ToList();

        if (tokens.Count == 0)
        {
            throw new InvalidOperationException(T("ExceptionOnvifNoMediaProfile"));
        }

        var ptzProfile = profiles.FirstOrDefault(x => x.HasPtzConfiguration);
        if (!string.IsNullOrWhiteSpace(ptzProfile.Token))
        {
            return ptzProfile.Token;
        }

        return tokens[0];
    }

    private static async Task<(int? Width, int? Height)> ResolveProfileResolutionAsync(Uri mediaServiceUri, string profileToken, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileToken))
        {
            return (null, null);
        }

        try
        {
            var profiles = await TryResolveProfilesAsync(mediaServiceUri, credentials, cancellationToken).ConfigureAwait(false);
            var profile = profiles.FirstOrDefault(x => string.Equals(x.Token, profileToken, StringComparison.Ordinal));
            var width = profile.Width is > 0 ? profile.Width : null;
            var height = profile.Height is > 0 ? profile.Height : null;
            return (width, height);
        }
        catch
        {
            return (null, null);
        }
    }

    public void InvalidateCache()
    {
        lock (this._ptzContextSync)
        {
            this._cachedPtzContext = null;
        }
    }

    private static async Task<XDocument> ResolveCapabilitiesDocumentAsync(Uri deviceServiceUri, NetworkCredential? credentials, CancellationToken cancellationToken, string category)
    {
        const string action = "http://www.onvif.org/ver10/device/wsdl/GetCapabilities";

        try
        {
            var safeCategory = SecurityElement.Escape(category) ?? "All";
            var body = $"<tds:GetCapabilities xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"><tds:Category>{safeCategory}</tds:Category></tds:GetCapabilities>";
            return await SendSoapRequestAsync(deviceServiceUri, action, body, credentials, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            const string fallbackBody = "<tds:GetCapabilities xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"><tds:Category>All</tds:Category></tds:GetCapabilities>";
            return await SendSoapRequestAsync(deviceServiceUri, action, fallbackBody, credentials, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ResolveStreamUriAsync(Uri mediaServiceUri, string profileToken, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        var token = SecurityElement.Escape(profileToken);
        var media10Action = "http://www.onvif.org/ver10/media/wsdl/GetStreamUri";
        var media10Body = $"<trt:GetStreamUri xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" xmlns:tt=\"http://www.onvif.org/ver10/schema\"><trt:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream><tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></trt:StreamSetup><trt:ProfileToken>{token}</trt:ProfileToken></trt:GetStreamUri>";

        XDocument document;
        try
        {
            document = await SendSoapRequestAsync(mediaServiceUri, media10Action, media10Body, credentials, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var media20Action = "http://www.onvif.org/ver20/media/wsdl/GetStreamUri";
            var media20Body = $"<tr2:GetStreamUri xmlns:tr2=\"http://www.onvif.org/ver20/media/wsdl\"><tr2:Protocol>RTSP</tr2:Protocol><tr2:ProfileToken>{token}</tr2:ProfileToken></tr2:GetStreamUri>";
            document = await SendSoapRequestAsync(mediaServiceUri, media20Action, media20Body, credentials, cancellationToken).ConfigureAwait(false);
        }

        var mediaUriNode = document.Descendants().FirstOrDefault(x => string.Equals(x.Name.LocalName, "MediaUri", StringComparison.OrdinalIgnoreCase));
        var streamUri = mediaUriNode?.Descendants().FirstOrDefault(x => string.Equals(x.Name.LocalName, "Uri", StringComparison.OrdinalIgnoreCase))?.Value
                        ?? document.Descendants().FirstOrDefault(x => string.Equals(x.Name.LocalName, "Uri", StringComparison.OrdinalIgnoreCase) && x.Value.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))?.Value;

        if (string.IsNullOrWhiteSpace(streamUri))
        {
            throw new InvalidOperationException(T("ExceptionOnvifGetStreamUriMissing"));
        }

        return streamUri.Trim();
    }

    private static async Task<List<OnvifPreset>> ResolvePresetsFromPtzAsync(Uri ptzServiceUri, string profileToken, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        var profileTokenEscaped = SecurityElement.Escape(profileToken);
        var action = "http://www.onvif.org/ver20/ptz/wsdl/GetPresets";
        var body = $"<tptz:GetPresets xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\"><tptz:ProfileToken>{profileTokenEscaped}</tptz:ProfileToken></tptz:GetPresets>";

        XDocument document;
        try
        {
            document = await SendSoapRequestAsync(ptzServiceUri, action, body, credentials, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var action10 = "http://www.onvif.org/ver10/ptz/wsdl/GetPresets";
            var body10 = $"<tptz:GetPresets xmlns:tptz=\"http://www.onvif.org/ver10/ptz/wsdl\"><tptz:ProfileToken>{profileTokenEscaped}</tptz:ProfileToken></tptz:GetPresets>";
            document = await SendSoapRequestAsync(ptzServiceUri, action10, body10, credentials, cancellationToken).ConfigureAwait(false);
        }

        return document
            .Descendants()
            .Where(x => string.Equals(x.Name.LocalName, "Preset", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Name.LocalName, "Presets", StringComparison.OrdinalIgnoreCase))
            .Select(x =>
            {
                var token = x.Attribute("token")?.Value?.Trim() ?? string.Empty;
                var name = x.Descendants().FirstOrDefault(d => string.Equals(d.Name.LocalName, "Name", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = token;
                }

                return new OnvifPreset
                {
                    Token = token,
                    Name = name ?? string.Empty
                };
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Token))
            .ToList();
    }

    private static async Task SendGotoPresetAsync(Uri ptzServiceUri, string profileToken, string presetToken, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        var profileTokenEscaped = SecurityElement.Escape(profileToken);
        var presetTokenEscaped = SecurityElement.Escape(presetToken);
        var action = "http://www.onvif.org/ver20/ptz/wsdl/GotoPreset";
        var body = $"<tptz:GotoPreset xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\"><tptz:ProfileToken>{profileTokenEscaped}</tptz:ProfileToken><tptz:PresetToken>{presetTokenEscaped}</tptz:PresetToken></tptz:GotoPreset>";

        try
        {
            await SendSoapRequestAsync(ptzServiceUri, action, body, credentials, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var action10 = "http://www.onvif.org/ver10/ptz/wsdl/GotoPreset";
            var body10 = $"<tptz:GotoPreset xmlns:tptz=\"http://www.onvif.org/ver10/ptz/wsdl\"><tptz:ProfileToken>{profileTokenEscaped}</tptz:ProfileToken><tptz:PresetToken>{presetTokenEscaped}</tptz:PresetToken></tptz:GotoPreset>";
            await SendSoapRequestAsync(ptzServiceUri, action10, body10, credentials, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendStopAsync(Uri ptzServiceUri, string profileToken, bool stopPanTilt, bool stopZoom, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        var profileTokenEscaped = SecurityElement.Escape(profileToken);
        var panTilt = stopPanTilt ? "true" : "false";
        var zoom = stopZoom ? "true" : "false";
        var action = "http://www.onvif.org/ver20/ptz/wsdl/Stop";
        var body = $"<tptz:Stop xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\"><tptz:ProfileToken>{profileTokenEscaped}</tptz:ProfileToken><tptz:PanTilt>{panTilt}</tptz:PanTilt><tptz:Zoom>{zoom}</tptz:Zoom></tptz:Stop>";

        try
        {
            await SendSoapRequestAsync(ptzServiceUri, action, body, credentials, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var action10 = "http://www.onvif.org/ver10/ptz/wsdl/Stop";
            var body10 = $"<tptz:Stop xmlns:tptz=\"http://www.onvif.org/ver10/ptz/wsdl\"><tptz:ProfileToken>{profileTokenEscaped}</tptz:ProfileToken><tptz:PanTilt>{panTilt}</tptz:PanTilt><tptz:Zoom>{zoom}</tptz:Zoom></tptz:Stop>";
            await SendSoapRequestAsync(ptzServiceUri, action10, body10, credentials, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendRelativeZoomAsync(Uri ptzServiceUri, string profileToken, double zoomDelta, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        var profileTokenEscaped = SecurityElement.Escape(profileToken);
        var zoom = zoomDelta.ToString("0.###", CultureInfo.InvariantCulture);
        var action = "http://www.onvif.org/ver20/ptz/wsdl/RelativeMove";
        var body = $"<tptz:RelativeMove xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\" xmlns:tt=\"http://www.onvif.org/ver10/schema\"><tptz:ProfileToken>{profileTokenEscaped}</tptz:ProfileToken><tptz:Translation><tt:Zoom x=\"{zoom}\" /></tptz:Translation></tptz:RelativeMove>";

        try
        {
            await SendSoapRequestAsync(ptzServiceUri, action, body, credentials, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var action10 = "http://www.onvif.org/ver10/ptz/wsdl/RelativeMove";
            var body10 = $"<tptz:RelativeMove xmlns:tptz=\"http://www.onvif.org/ver10/ptz/wsdl\" xmlns:tt=\"http://www.onvif.org/ver10/schema\"><tptz:ProfileToken>{profileTokenEscaped}</tptz:ProfileToken><tptz:Translation><tt:Zoom x=\"{zoom}\" /></tptz:Translation></tptz:RelativeMove>";
            await SendSoapRequestAsync(ptzServiceUri, action10, body10, credentials, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PtzContext> ResolvePtzContextAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var cacheKey = BuildPtzContextCacheKey(settings);

        lock (this._ptzContextSync)
        {
            if (this._cachedPtzContext is not null && string.Equals(this._cachedPtzContext.CacheKey, cacheKey, StringComparison.Ordinal))
            {
                return this._cachedPtzContext;
            }
        }

        var endpoint = GetRequiredOnvifDeviceServiceUri(settings);
        var credentials = ResolveCredentials(settings);
        try
        {
            var mediaServiceUrl = await ResolveMediaServiceUrlAsync(endpoint, credentials, cancellationToken).ConfigureAwait(false);
            var mediaServiceUri = BuildServiceUriUsingConfiguredAuthority(endpoint, mediaServiceUrl, "Media");
            var profileToken = await ResolveProfileTokenAsync(mediaServiceUri, settings.OnvifProfileToken, credentials, cancellationToken).ConfigureAwait(false);
            var ptzServiceUrl = await ResolvePtzServiceUrlAsync(endpoint, credentials, cancellationToken).ConfigureAwait(false);
            var ptzServiceUri = BuildServiceUriUsingConfiguredAuthority(endpoint, ptzServiceUrl, "PTZ");

            var context = new PtzContext
            {
                CacheKey = cacheKey,
                MediaServiceUri = mediaServiceUri,
                ProfileToken = profileToken,
                Credentials = credentials,
                PtzCandidates = [ptzServiceUri]
            };

            lock (this._ptzContextSync)
            {
                this._cachedPtzContext = context;
            }

            return context;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(TF("ExceptionOnvifResolvePtzContextFailedWithError", ex.Message), ex);
        }
    }

    private static async Task<OnvifPtzCapabilities> ResolvePtzCapabilitiesAsync(Uri ptzServiceUri, Uri mediaServiceUri, string profileToken, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        var ptzConfigurationToken = await ResolvePtzConfigurationTokenAsync(mediaServiceUri, profileToken, credentials, cancellationToken).ConfigureAwait(false);
        var options = await TryGetPtzConfigurationOptionsAsync(ptzServiceUri, profileToken, ptzConfigurationToken, credentials, cancellationToken).ConfigureAwait(false);
        var status = await TryGetPtzStatusAsync(ptzServiceUri, profileToken, credentials, cancellationToken).ConfigureAwait(false);

        var panRange = ResolveAxisRange(options, "RelativePanTiltTranslationSpace", "XRange");
        var tiltRange = ResolveAxisRange(options, "RelativePanTiltTranslationSpace", "YRange");
        var zoomRange = ResolveAxisRange(options, "RelativeZoomTranslationSpace", "XRange");

        var hasPan = panRange.HasRange || HasElement(options, "RelativePanTiltTranslationSpace");
        var hasTilt = tiltRange.HasRange || HasElement(options, "RelativePanTiltTranslationSpace");
        var hasZoom = zoomRange.HasRange || HasElement(options, "RelativeZoomTranslationSpace");

        var currentZoom = ResolveStatusAxisValue(status, "Zoom", "x");
        var statusPan = ResolveStatusAxisValue(status, "PanTilt", "x");
        var statusTilt = ResolveStatusAxisValue(status, "PanTilt", "y");

        if (!hasPan && statusPan.HasValue)
        {
            hasPan = true;
        }

        if (!hasTilt && statusTilt.HasValue)
        {
            hasTilt = true;
        }

        if (!hasZoom && currentZoom.HasValue)
        {
            hasZoom = true;
        }

        var normalizedZoom = NormalizeValue(currentZoom, zoomRange.Min, zoomRange.Max);
        var panMinStep = ResolveMinimumStep(panRange.Min, panRange.Max, DefaultPanTiltMinStep);
        var tiltMinStep = ResolveMinimumStep(tiltRange.Min, tiltRange.Max, DefaultPanTiltMinStep);
        var zoomMinStep = ResolveMinimumStep(zoomRange.Min, zoomRange.Max, DefaultZoomMinStep);

        if (!hasPan)
        {
            panMinStep = null;
        }

        if (!hasTilt)
        {
            tiltMinStep = null;
        }

        if (!hasZoom)
        {
            zoomMinStep = null;
        }

        return new OnvifPtzCapabilities
        {
            HasPan = hasPan,
            HasTilt = hasTilt,
            HasZoom = hasZoom,
            PanMin = panRange.Min,
            PanMax = panRange.Max,
            TiltMin = tiltRange.Min,
            TiltMax = tiltRange.Max,
            ZoomMin = zoomRange.Min,
            ZoomMax = zoomRange.Max,
            PanMinStep = panMinStep,
            TiltMinStep = tiltMinStep,
            ZoomMinStep = zoomMinStep,
            CurrentZoom = currentZoom,
            CurrentZoomNormalized = normalizedZoom
        };
    }

    private static async Task<string?> ResolvePtzConfigurationTokenAsync(Uri mediaServiceUri, string profileToken, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        var escapedToken = SecurityElement.Escape(profileToken);
        if (string.IsNullOrWhiteSpace(escapedToken))
        {
            return null;
        }

        var media10Action = "http://www.onvif.org/ver10/media/wsdl/GetProfile";
        var media10Body = $"<trt:GetProfile xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\"><trt:ProfileToken>{escapedToken}</trt:ProfileToken></trt:GetProfile>";

        XDocument document;
        try
        {
            document = await SendSoapRequestAsync(mediaServiceUri, media10Action, media10Body, credentials, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var media20Action = "http://www.onvif.org/ver20/media/wsdl/GetProfile";
            var media20Body = $"<tr2:GetProfile xmlns:tr2=\"http://www.onvif.org/ver20/media/wsdl\"><tr2:Token>{escapedToken}</tr2:Token></tr2:GetProfile>";
            document = await SendSoapRequestAsync(mediaServiceUri, media20Action, media20Body, credentials, cancellationToken).ConfigureAwait(false);
        }

        var profileNode = document.Descendants().FirstOrDefault(x =>
            string.Equals(x.Name.LocalName, "Profile", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Name.LocalName, "Profiles", StringComparison.OrdinalIgnoreCase));

        var ptzToken = profileNode?
            .Descendants()
            .FirstOrDefault(x => string.Equals(x.Name.LocalName, "PTZConfiguration", StringComparison.OrdinalIgnoreCase))?
            .Attribute("token")?.Value;

        if (!string.IsNullOrWhiteSpace(ptzToken))
        {
            return ptzToken.Trim();
        }

        return profileNode?
            .Descendants()
            .FirstOrDefault(x => string.Equals(x.Name.LocalName, "PTZConfigurationToken", StringComparison.OrdinalIgnoreCase))?
            .Value?.Trim();
    }

    private static async Task<XDocument?> TryGetPtzConfigurationOptionsAsync(Uri ptzServiceUri, string profileToken, string? configurationToken, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileToken))
        {
            return null;
        }

        var escapedProfile = SecurityElement.Escape(profileToken);
        var escapedConfig = SecurityElement.Escape(configurationToken);
        var configTokenNode = string.IsNullOrWhiteSpace(escapedConfig) ? string.Empty : $"<tptz:ConfigurationToken>{escapedConfig}</tptz:ConfigurationToken>";
        var profileTokenNode = $"<tptz:ProfileToken>{escapedProfile}</tptz:ProfileToken>";

        var action = "http://www.onvif.org/ver20/ptz/wsdl/GetConfigurationOptions";
        var body = $"<tptz:GetConfigurationOptions xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\">{configTokenNode}{profileTokenNode}</tptz:GetConfigurationOptions>";

        try
        {
            return await SendSoapRequestAsync(ptzServiceUri, action, body, credentials, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var action10 = "http://www.onvif.org/ver10/ptz/wsdl/GetConfigurationOptions";
            var body10 = $"<tptz:GetConfigurationOptions xmlns:tptz=\"http://www.onvif.org/ver10/ptz/wsdl\">{configTokenNode}{profileTokenNode}</tptz:GetConfigurationOptions>";
            try
            {
                return await SendSoapRequestAsync(ptzServiceUri, action10, body10, credentials, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }
    }

    private static async Task<XDocument?> TryGetPtzStatusAsync(Uri ptzServiceUri, string profileToken, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileToken))
        {
            return null;
        }

        var escapedProfile = SecurityElement.Escape(profileToken);
        var action = "http://www.onvif.org/ver20/ptz/wsdl/GetStatus";
        var body = $"<tptz:GetStatus xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\"><tptz:ProfileToken>{escapedProfile}</tptz:ProfileToken></tptz:GetStatus>";

        try
        {
            return await SendSoapRequestAsync(ptzServiceUri, action, body, credentials, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var action10 = "http://www.onvif.org/ver10/ptz/wsdl/GetStatus";
            var body10 = $"<tptz:GetStatus xmlns:tptz=\"http://www.onvif.org/ver10/ptz/wsdl\"><tptz:ProfileToken>{escapedProfile}</tptz:ProfileToken></tptz:GetStatus>";
            try
            {
                return await SendSoapRequestAsync(ptzServiceUri, action10, body10, credentials, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }
    }

    private static (double? Min, double? Max, bool HasRange) ResolveAxisRange(XDocument? document, string spaceNodeName, string rangeNodeName)
    {
        if (document is null)
        {
            return (null, null, false);
        }

        foreach (var spaceNode in document.Descendants().Where(x => string.Equals(x.Name.LocalName, spaceNodeName, StringComparison.OrdinalIgnoreCase)))
        {
            var rangeNode = spaceNode.Descendants().FirstOrDefault(x => string.Equals(x.Name.LocalName, rangeNodeName, StringComparison.OrdinalIgnoreCase));
            var min = TryParseRangeBoundary(rangeNode, "Min");
            var max = TryParseRangeBoundary(rangeNode, "Max");

            if (min.HasValue || max.HasValue)
            {
                return (min, max, true);
            }
        }

        return (null, null, false);
    }

    private static bool HasElement(XDocument? document, string localName)
    {
        return document is not null &&
               document.Descendants().Any(x => string.Equals(x.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
    }

    private static double? TryParseRangeBoundary(XElement? rangeNode, string boundaryName)
    {
        var valueNode = rangeNode?.Descendants().FirstOrDefault(x => string.Equals(x.Name.LocalName, boundaryName, StringComparison.OrdinalIgnoreCase));
        if (double.TryParse(valueNode?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? ResolveStatusAxisValue(XDocument? document, string axisNodeName, string attributeName)
    {
        if (document is null)
        {
            return null;
        }

        var axisNode = document.Descendants().FirstOrDefault(x => string.Equals(x.Name.LocalName, axisNodeName, StringComparison.OrdinalIgnoreCase));
        if (axisNode is null)
        {
            return null;
        }

        var attribute = axisNode.Attribute(attributeName) ?? axisNode.Attribute(attributeName.ToUpperInvariant());
        if (attribute is null)
        {
            return null;
        }

        return double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? NormalizeValue(double? value, double? min, double? max)
    {
        if (!value.HasValue || !min.HasValue || !max.HasValue)
        {
            return null;
        }

        var span = max.Value - min.Value;
        if (span <= 0)
        {
            return null;
        }

        return Math.Clamp((value.Value - min.Value) / span, 0d, 1d);
    }

    private static double? ResolveMinimumStep(double? min, double? max, double fallback)
    {
        if (!min.HasValue || !max.HasValue || max.Value <= min.Value)
        {
            return fallback;
        }

        var step = (max.Value - min.Value) / 1000d;
        if (double.IsNaN(step) || double.IsInfinity(step) || step <= 0)
        {
            return fallback;
        }

        return Math.Min(Math.Max(step, fallback / 10d), Math.Max(fallback, step));
    }

    private static async Task SendRelativeMoveAsync(Uri ptzServiceUri, string profileToken, double panDelta, double tiltDelta, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        var profileTokenEscaped = SecurityElement.Escape(profileToken);
        var pan = panDelta.ToString("0.###", CultureInfo.InvariantCulture);
        var tilt = tiltDelta.ToString("0.###", CultureInfo.InvariantCulture);
        var action = "http://www.onvif.org/ver20/ptz/wsdl/RelativeMove";
        var body = $"<tptz:RelativeMove xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\" xmlns:tt=\"http://www.onvif.org/ver10/schema\"><tptz:ProfileToken>{profileTokenEscaped}</tptz:ProfileToken><tptz:Translation><tt:PanTilt x=\"{pan}\" y=\"{tilt}\" /></tptz:Translation></tptz:RelativeMove>";

        try
        {
            await SendSoapRequestAsync(ptzServiceUri, action, body, credentials, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var action10 = "http://www.onvif.org/ver10/ptz/wsdl/RelativeMove";
            var body10 = $"<tptz:RelativeMove xmlns:tptz=\"http://www.onvif.org/ver10/ptz/wsdl\" xmlns:tt=\"http://www.onvif.org/ver10/schema\"><tptz:ProfileToken>{profileTokenEscaped}</tptz:ProfileToken><tptz:Translation><tt:PanTilt x=\"{pan}\" y=\"{tilt}\" /></tptz:Translation></tptz:RelativeMove>";
            await SendSoapRequestAsync(ptzServiceUri, action10, body10, credentials, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildPtzContextCacheKey(AppSettings settings)
    {
        return string.Join("|",
            settings.OnvifDeviceServiceUrl ?? string.Empty,
            settings.Url ?? string.Empty,
            settings.OnvifProfileToken ?? string.Empty,
            settings.Username ?? string.Empty,
            settings.Password ?? string.Empty);
    }

    private static async Task<XDocument> SendSoapRequestAsync(Uri endpoint, string action, string soapBody, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        try
        {
            return await SendSoapRequestAsync(endpoint, action, soapBody, credentials, cancellationToken, useSoap12: true).ConfigureAwait(false);
        }
        catch
        {
            return await SendSoapRequestAsync(endpoint, action, soapBody, credentials, cancellationToken, useSoap12: false).ConfigureAwait(false);
        }
    }

    private static async Task<XDocument> SendSoapRequestAsync(Uri endpoint, string action, string soapBody, NetworkCredential? credentials, CancellationToken cancellationToken, bool useSoap12)
    {
        using var handler = new HttpClientHandler();
        handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        if (credentials is not null)
        {
            handler.Credentials = credentials;
            handler.PreAuthenticate = false;
        }

        using var client = new HttpClient(handler) { Timeout = SoapRequestTimeout };
        var envelope = BuildSoapEnvelope(soapBody, useSoap12, credentials);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(envelope, Encoding.UTF8)
        };

        if (useSoap12)
        {
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"application/soap+xml; charset=utf-8; action=\"{action}\"");
        }
        else
        {
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{action}\"");
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(TF("ExceptionOnvifRequestFailed", (int)response.StatusCode, response.ReasonPhrase));
        }

        var document = XDocument.Parse(payload);
        var fault = document.Descendants().FirstOrDefault(x => string.Equals(x.Name.LocalName, "Fault", StringComparison.OrdinalIgnoreCase));
        if (fault is not null)
        {
            throw new InvalidOperationException(T("ExceptionOnvifSoapFault"));
        }

        return document;
    }

    private static async Task<List<(string Token, bool HasPtzConfiguration, int? Width, int? Height)>> TryResolveProfilesAsync(Uri mediaServiceUri, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        var media10Action = "http://www.onvif.org/ver10/media/wsdl/GetProfiles";
        var media10Body = "<trt:GetProfiles xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" />";

        try
        {
            var media10 = await SendSoapRequestAsync(mediaServiceUri, media10Action, media10Body, credentials, cancellationToken).ConfigureAwait(false);
            var media10Profiles = ExtractProfiles(media10);
            if (media10Profiles.Count > 0)
            {
                return media10Profiles;
            }
        }
        catch
        {
        }

        var media20Action = "http://www.onvif.org/ver20/media/wsdl/GetProfiles";
        var media20Body = "<tr2:GetProfiles xmlns:tr2=\"http://www.onvif.org/ver20/media/wsdl\" />";
        var media20 = await SendSoapRequestAsync(mediaServiceUri, media20Action, media20Body, credentials, cancellationToken).ConfigureAwait(false);
        return ExtractProfiles(media20);
    }

    private static List<(string Token, bool HasPtzConfiguration, int? Width, int? Height)> ExtractProfiles(XDocument document)
    {
        return document
            .Descendants()
            .Where(x => string.Equals(x.Name.LocalName, "Profiles", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Name.LocalName, "Profile", StringComparison.OrdinalIgnoreCase))
            .Select(x =>
            {
                var token = x.Attribute("token")?.Value;
                var hasPtz = x.Descendants().Any(d => string.Equals(d.Name.LocalName, "PTZConfiguration", StringComparison.OrdinalIgnoreCase));
                var resolutionNode = x.Descendants().FirstOrDefault(d => string.Equals(d.Name.LocalName, "Resolution", StringComparison.OrdinalIgnoreCase));
                var widthNode = resolutionNode?.Descendants().FirstOrDefault(d => string.Equals(d.Name.LocalName, "Width", StringComparison.OrdinalIgnoreCase));
                var heightNode = resolutionNode?.Descendants().FirstOrDefault(d => string.Equals(d.Name.LocalName, "Height", StringComparison.OrdinalIgnoreCase));
                var width = int.TryParse(widthNode?.Value, out var parsedWidth) && parsedWidth > 0 ? (int?)parsedWidth : null;
                var height = int.TryParse(heightNode?.Value, out var parsedHeight) && parsedHeight > 0 ? (int?)parsedHeight : null;
                return (Token: token, HasPtzConfiguration: hasPtz, Width: width, Height: height);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Token))
            .Select(x => (x.Token!, x.HasPtzConfiguration, x.Width, x.Height))
            .ToList();
    }

    private static string BuildSoapEnvelope(string soapBody, bool useSoap12, NetworkCredential? credentials)
    {
        var envelopeNs = useSoap12 ? "http://www.w3.org/2003/05/soap-envelope" : "http://schemas.xmlsoap.org/soap/envelope/";
        var wsSecurityHeader = BuildWsSecurityHeader(credentials);
        var header = string.IsNullOrWhiteSpace(wsSecurityHeader) ? string.Empty : $"<soap:Header>{wsSecurityHeader}</soap:Header>";
        return $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"{envelopeNs}\">{header}<soap:Body>{soapBody}</soap:Body></soap:Envelope>";
    }

    private static string BuildWsSecurityHeader(NetworkCredential? credentials)
    {
        if (credentials is null || string.IsNullOrWhiteSpace(credentials.UserName))
        {
            return string.Empty;
        }

        var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var nonceBytes = RandomNumberGenerator.GetBytes(16);
        var createdBytes = Encoding.UTF8.GetBytes(created);
        var passwordBytes = Encoding.UTF8.GetBytes(credentials.Password ?? string.Empty);

        var digestInput = new byte[nonceBytes.Length + createdBytes.Length + passwordBytes.Length];
        Buffer.BlockCopy(nonceBytes, 0, digestInput, 0, nonceBytes.Length);
        Buffer.BlockCopy(createdBytes, 0, digestInput, nonceBytes.Length, createdBytes.Length);
        Buffer.BlockCopy(passwordBytes, 0, digestInput, nonceBytes.Length + createdBytes.Length, passwordBytes.Length);

        var passwordDigest = Convert.ToBase64String(SHA1.HashData(digestInput));
        var nonceBase64 = Convert.ToBase64String(nonceBytes);
        var username = SecurityElement.Escape(credentials.UserName) ?? string.Empty;

        return $"<wsse:Security soap:mustUnderstand=\"1\" xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\" xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\"><wsse:UsernameToken><wsse:Username>{username}</wsse:Username><wsse:Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">{passwordDigest}</wsse:Password><wsse:Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">{nonceBase64}</wsse:Nonce><wsu:Created>{created}</wsu:Created></wsse:UsernameToken></wsse:Security>";
    }
}
