using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sing_box_UI
{
    internal static class SingBoxUpdateService
    {
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/SagerNet/sing-box/releases/latest";

        public static string GetInstalledVersion(string singBoxPath, string workingDirectory)
        {
            using (var process = Process.Start(new ProcessStartInfo
            {
                FileName = singBoxPath,
                Arguments = "version",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("sing-box process was not created.");
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                var firstLine = output
                    .Replace("\r", string.Empty)
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

                if (firstLine.Length == 0)
                {
                    throw new InvalidOperationException("Unable to read sing-box version." + Environment.NewLine + error);
                }

                var match = Regex.Match(firstLine[0], @"\b\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]+)?\b");
                if (!match.Success)
                {
                    throw new InvalidOperationException("Unable to parse sing-box version from: " + firstLine[0]);
                }

                return match.Value;
            }
        }

        public static async Task<GitHubReleaseInfo> GetLatestReleaseAsync(CancellationToken cancellationToken)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = Timeout.InfiniteTimeSpan;
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SingBoxUI/1.0");
                httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                using (var response = await httpClient.GetAsync(LatestReleaseApiUrl, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    using (var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(GitHubReleaseInfo));
                        var release = serializer.ReadObject(responseStream) as GitHubReleaseInfo;
                        if (release == null)
                        {
                            throw new InvalidOperationException("GitHub latest release response is empty.");
                        }

                        return release;
                    }
                }
            }
        }

        public static bool IsLatestVersionNewer(string currentVersion, string latestVersion)
        {
            return CompareVersions(NormalizeVersion(latestVersion), NormalizeVersion(currentVersion)) > 0;
        }

        public static GitHubReleaseAsset GetPreferredWindowsAsset(GitHubReleaseInfo release)
        {
            var architectureSuffix = GetPreferredArchitectureSuffix();

            var asset = release.Assets?.FirstOrDefault(candidate =>
                candidate.Name != null &&
                candidate.Name.EndsWith("-" + architectureSuffix + ".zip", StringComparison.OrdinalIgnoreCase));

            if (asset != null)
            {
                return asset;
            }

            return release.Assets?.FirstOrDefault(candidate =>
                candidate.Name != null &&
                candidate.Name.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0 &&
                candidate.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        public static async Task DownloadAndInstallAsync(
            GitHubReleaseAsset asset,
            string targetDirectory,
            string tempZipPath,
            TimeSpan receiveTimeout,
            Action beforeInstallCallback,
            Action<long, long, double> progressCallback,
            CancellationToken cancellationToken)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var existingBytes = File.Exists(tempZipPath) ? new FileInfo(tempZipPath).Length : 0L;

            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = Timeout.InfiniteTimeSpan;
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SingBoxUI/1.0");
                httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");

                var request = new HttpRequestMessage(HttpMethod.Get, asset.BrowserDownloadUrl);
                if (existingBytes > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);
                }

                using (request)
                using (var response = await SendWithReceiveTimeoutAsync(
                    httpClient,
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    receiveTimeout,
                    cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    var isPartialResponse = response.StatusCode == HttpStatusCode.PartialContent && existingBytes > 0;
                    if (existingBytes > 0 && !isPartialResponse)
                    {
                        existingBytes = 0;
                    }

                    var totalBytes = response.Content.Headers.ContentRange?.Length
                        ?? (response.Content.Headers.ContentLength.HasValue
                            ? existingBytes + response.Content.Headers.ContentLength.Value
                            : 0);

                    var stopwatch = Stopwatch.StartNew();
                    long downloadedBytes = existingBytes;

                    using (var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var fileStream = new FileStream(
                        tempZipPath,
                        existingBytes > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write,
                        FileShare.None))
                    {
                        var buffer = new byte[81920];
                        while (true)
                        {
                            var bytesRead = await ReadWithReceiveTimeoutAsync(
                                responseStream,
                                buffer,
                                receiveTimeout,
                                cancellationToken).ConfigureAwait(false);
                            if (bytesRead == 0)
                            {
                                break;
                            }

                            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                            downloadedBytes += bytesRead;

                            var elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.1);
                            var bytesPerSecond = (downloadedBytes - existingBytes) / elapsedSeconds;
                            progressCallback(downloadedBytes, totalBytes, bytesPerSecond);
                        }
                    }
                }
            }

            beforeInstallCallback?.Invoke();
            ExtractZipToDirectory(tempZipPath, targetDirectory);
        }

        public static string NormalizeVersion(string version)
        {
            return string.IsNullOrWhiteSpace(version)
                ? string.Empty
                : version.Trim().TrimStart('v', 'V');
        }

        private static int CompareVersions(string leftVersion, string rightVersion)
        {
            var leftParts = SplitVersion(leftVersion);
            var rightParts = SplitVersion(rightVersion);

            var maxMainLength = Math.Max(leftParts.MainSegments.Length, rightParts.MainSegments.Length);
            for (var index = 0; index < maxMainLength; index++)
            {
                var left = index < leftParts.MainSegments.Length ? leftParts.MainSegments[index] : 0;
                var right = index < rightParts.MainSegments.Length ? rightParts.MainSegments[index] : 0;
                if (left != right)
                {
                    return left.CompareTo(right);
                }
            }

            if (leftParts.PreReleaseSegments.Length == 0 && rightParts.PreReleaseSegments.Length == 0)
            {
                return 0;
            }

            if (leftParts.PreReleaseSegments.Length == 0)
            {
                return 1;
            }

            if (rightParts.PreReleaseSegments.Length == 0)
            {
                return -1;
            }

            var maxPreReleaseLength = Math.Max(leftParts.PreReleaseSegments.Length, rightParts.PreReleaseSegments.Length);
            for (var index = 0; index < maxPreReleaseLength; index++)
            {
                if (index >= leftParts.PreReleaseSegments.Length)
                {
                    return -1;
                }

                if (index >= rightParts.PreReleaseSegments.Length)
                {
                    return 1;
                }

                var leftSegment = leftParts.PreReleaseSegments[index];
                var rightSegment = rightParts.PreReleaseSegments[index];

                var leftIsNumeric = int.TryParse(leftSegment, out var leftNumeric);
                var rightIsNumeric = int.TryParse(rightSegment, out var rightNumeric);

                if (leftIsNumeric && rightIsNumeric)
                {
                    if (leftNumeric != rightNumeric)
                    {
                        return leftNumeric.CompareTo(rightNumeric);
                    }

                    continue;
                }

                if (leftIsNumeric != rightIsNumeric)
                {
                    return leftIsNumeric ? -1 : 1;
                }

                var compareResult = string.Compare(leftSegment, rightSegment, StringComparison.OrdinalIgnoreCase);
                if (compareResult != 0)
                {
                    return compareResult;
                }
            }

            return 0;
        }

        private static ParsedVersion SplitVersion(string version)
        {
            var mainAndSuffix = (version ?? string.Empty).Split(new[] { '-' }, 2);
            var mainSegments = mainAndSuffix[0]
                .Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);

            var parsedMainSegments = new int[mainSegments.Length];
            for (var index = 0; index < mainSegments.Length; index++)
            {
                int.TryParse(mainSegments[index], out parsedMainSegments[index]);
            }

            var preReleaseSegments = mainAndSuffix.Length > 1
                ? mainAndSuffix[1].Split(new[] { '.', '-' }, StringSplitOptions.RemoveEmptyEntries)
                : new string[0];

            return new ParsedVersion(parsedMainSegments, preReleaseSegments);
        }

        private static string GetPreferredArchitectureSuffix()
        {
            var architecture = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? string.Empty).ToLowerInvariant();
            if (architecture.Contains("arm64"))
            {
                return "windows-arm64";
            }

            return Environment.Is64BitOperatingSystem ? "windows-amd64" : "windows-386";
        }

        private static void ExtractZipToDirectory(string zipPath, string targetDirectory)
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var sharedRootDirectory = GetSharedRootDirectory(archive.Entries
                    .Select(entry => NormalizeArchivePath(entry.FullName))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToArray());
                var normalizedTargetDirectory = Path.GetFullPath(targetDirectory);

                foreach (var entry in archive.Entries)
                {
                    var relativePath = NormalizeArchivePath(entry.FullName);
                    if (!string.IsNullOrWhiteSpace(sharedRootDirectory))
                    {
                        relativePath = TrimSharedRootDirectory(relativePath, sharedRootDirectory);
                    }

                    if (string.IsNullOrWhiteSpace(relativePath))
                    {
                        continue;
                    }

                    var destinationPath = Path.GetFullPath(Path.Combine(targetDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                    if (!destinationPath.StartsWith(normalizedTargetDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(destinationPath, normalizedTargetDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Archive entry points outside the target directory: " + entry.FullName);
                    }

                    var destinationDirectory = Path.GetDirectoryName(destinationPath);

                    if (!string.IsNullOrWhiteSpace(destinationDirectory))
                    {
                        Directory.CreateDirectory(destinationDirectory);
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }

                    entry.ExtractToFile(destinationPath, true);
                }
            }
        }

        private static async Task<HttpResponseMessage> SendWithReceiveTimeoutAsync(
            HttpClient httpClient,
            HttpRequestMessage request,
            HttpCompletionOption completionOption,
            TimeSpan receiveTimeout,
            CancellationToken cancellationToken)
        {
            using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutSource.CancelAfter(receiveTimeout);
                return await httpClient.SendAsync(request, completionOption, timeoutSource.Token).ConfigureAwait(false);
            }
        }

        private static async Task<int> ReadWithReceiveTimeoutAsync(
            Stream responseStream,
            byte[] buffer,
            TimeSpan receiveTimeout,
            CancellationToken cancellationToken)
        {
            using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutSource.CancelAfter(receiveTimeout);
                return await responseStream.ReadAsync(buffer, 0, buffer.Length, timeoutSource.Token).ConfigureAwait(false);
            }
        }

        private static string NormalizeArchivePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim('/');
        }

        private static string GetSharedRootDirectory(string[] entryPaths)
        {
            if (entryPaths == null || entryPaths.Length == 0)
            {
                return null;
            }

            var firstSegments = entryPaths
                .Select(GetFirstPathSegment)
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return firstSegments.Length == 1 ? firstSegments[0] : null;
        }

        private static string GetFirstPathSegment(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var separatorIndex = path.IndexOf('/');
            return separatorIndex >= 0 ? path.Substring(0, separatorIndex) : path;
        }

        private static string TrimSharedRootDirectory(string path, string sharedRootDirectory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sharedRootDirectory))
            {
                return path;
            }

            if (string.Equals(path, sharedRootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var prefix = sharedRootDirectory + "/";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(prefix.Length)
                : path;
        }

        [DataContract]
        internal sealed class GitHubReleaseInfo
        {
            [DataMember(Name = "tag_name")]
            public string TagName { get; set; }

            [DataMember(Name = "body")]
            public string Body { get; set; }

            [DataMember(Name = "html_url")]
            public string HtmlUrl { get; set; }

            [DataMember(Name = "assets")]
            public GitHubReleaseAsset[] Assets { get; set; }
        }

        [DataContract]
        internal sealed class GitHubReleaseAsset
        {
            [DataMember(Name = "name")]
            public string Name { get; set; }

            [DataMember(Name = "browser_download_url")]
            public string BrowserDownloadUrl { get; set; }
        }

        private sealed class ParsedVersion
        {
            public ParsedVersion(int[] mainSegments, string[] preReleaseSegments)
            {
                MainSegments = mainSegments;
                PreReleaseSegments = preReleaseSegments;
            }

            public int[] MainSegments { get; }

            public string[] PreReleaseSegments { get; }
        }
    }
}
