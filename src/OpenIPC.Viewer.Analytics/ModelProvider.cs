using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Analytics;

// Resolves the detection model file (Phase 15 — download on first enable, cache
// in AppData, verify integrity, repo stays binary-free). Resolution order:
//   1. OPENIPC_DETECTION_MODEL env var (explicit local override / CI fixture),
//   2. cached file in {AppData}/models (re-verified if a hash is pinned),
//   3. download from the descriptor URI into the cache.
public sealed class ModelProvider : IModelProvider
{
    private readonly IFileSystem _fs;
    private readonly ILogger<ModelProvider> _log;
    private readonly ModelDescriptor _descriptor;
    private readonly Func<HttpClient> _httpFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ModelProvider(IFileSystem fs, ILogger<ModelProvider> log)
        : this(fs, log, ModelCatalog.Default, () => new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
    {
    }

    internal ModelProvider(IFileSystem fs, ILogger<ModelProvider> log,
        ModelDescriptor descriptor, Func<HttpClient> httpFactory)
    {
        _fs = fs;
        _log = log;
        _descriptor = descriptor;
        _httpFactory = httpFactory;
    }

    public async Task<ModelSpec> EnsureModelAsync(CancellationToken ct)
    {
        var overridePath = Environment.GetEnvironmentVariable("OPENIPC_DETECTION_MODEL");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            _log.LogInformation("Using detection model override: {Path}", overridePath);
            return _descriptor.CreateSpec(overridePath);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var modelsDir = Directory.CreateDirectory(Path.Combine(_fs.AppDataDir.FullName, "models"));
            var target = Path.Combine(modelsDir.FullName, _descriptor.FileName);

            if (File.Exists(target) && await VerifyAsync(target, ct).ConfigureAwait(false))
                return _descriptor.CreateSpec(target);

            if (_descriptor.DownloadUri is null)
                throw new InvalidOperationException(
                    $"Model {_descriptor.Name} is missing and no download URI is configured.");

            await DownloadAsync(_descriptor.DownloadUri, target, ct).ConfigureAwait(false);

            if (!await VerifyAsync(target, ct).ConfigureAwait(false))
            {
                File.Delete(target);
                throw new InvalidOperationException(
                    $"Downloaded model {_descriptor.Name} failed its integrity check.");
            }

            return _descriptor.CreateSpec(target);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> VerifyAsync(string path, CancellationToken ct)
    {
        if (_descriptor.Sha256Hex is null) return true; // not pinned yet
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        var hex = Convert.ToHexString(hash);
        var ok = string.Equals(hex, _descriptor.Sha256Hex, StringComparison.OrdinalIgnoreCase);
        if (!ok)
            _log.LogWarning("Model {Name} sha256 mismatch (expected {Expected}, got {Actual}).",
                _descriptor.Name, _descriptor.Sha256Hex, hex);
        return ok;
    }

    private async Task DownloadAsync(Uri uri, string target, CancellationToken ct)
    {
        _log.LogInformation("Downloading detection model {Name} from {Uri}", _descriptor.Name, uri);
        using var http = _httpFactory();
        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var tmp = target + ".part";
        await using (var dst = File.Create(tmp))
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
        File.Move(tmp, target, overwrite: true);
        _log.LogInformation("Detection model {Name} cached at {Path}", _descriptor.Name, target);
    }
}
