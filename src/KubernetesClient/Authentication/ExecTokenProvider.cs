using k8s.Exceptions;
using k8s.KubeConfigModels;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace k8s.Authentication
{
    public class ExecTokenProvider : ITokenProvider
    {
        public static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromMinutes(2);
        private readonly ExternalExecution exec;
        private readonly TimeSpan executionTimeout;
        private readonly bool interactive;
        private ExecCredentialResponse response;

        public ExecTokenProvider(ExternalExecution exec) : this(exec, DefaultExecutionTimeout) { }

        public ExecTokenProvider(ExternalExecution exec, TimeSpan executionTimeout, bool interactive = false)
        {
            this.exec = exec ?? throw new ArgumentNullException(nameof(exec));
            this.executionTimeout = executionTimeout;
            this.interactive = interactive;
        }

        private bool NeedsRefresh()
        {
            if (response?.Status == null)
            {
                return true;
            }

            if (response.Status.ExpirationTimestamp == null)
            {
                return false;
            }

            return DateTime.UtcNow.AddSeconds(30) > response.Status.ExpirationTimestamp;
        }

        public async Task<AuthenticationHeaderValue> GetAuthenticationHeaderAsync(CancellationToken cancellationToken)
        {
            if (NeedsRefresh())
            {
                await Task.Run(RefreshToken, cancellationToken).ConfigureAwait(false);
            }

            return new AuthenticationHeaderValue("Bearer", response.Status.Token);
        }

        internal ExecCredentialResponse RefreshToken() => response = ExecuteExternalCommandCore();

        /// <summary>
        /// Implementation of the proposal for out-of-tree client
        /// authentication providers as described here --
        /// https://github.com/kubernetes/community/blob/master/contributors/design-proposals/auth/kubectl-exec-plugins.md
        /// Took inspiration from python exec_provider.py --
        /// https://github.com/kubernetes-client/python-base/blob/master/config/exec_provider.py
        /// </summary>
        /// <returns>
        /// The token, client certificate data, and the client key data received from the external command execution
        /// </returns>
        private ExecCredentialResponse ExecuteExternalCommandCore()
        {
            using var process = CreateRunnableExternalProcess(exec, !interactive);
            StringBuilder stderr = new();

            try
            {
                process.Start();
                if (!interactive)
                {
                    process.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);
                    process.BeginErrorReadLine();
                }
            }
            catch (Exception ex)
            {
                throw new KubeConfigException($"external exec failed due to: {ex.Message}");
            }

            try
            {
                if (!process.WaitForExit((int)executionTimeout.TotalMilliseconds))
                {
                    process.Kill();
                    throw new KubeConfigException($"external exec failed due to timeout. stderr:\n{stderr}");
                }

                if (process.ExitCode != 0)
                {
                    throw new KubeConfigException($"external exec failed with exit code {process.ExitCode}. stderr:\n{stderr}");
                }

                var responseObject = KubernetesJson.Deserialize<ExecCredentialResponse>(process.StandardOutput.ReadToEnd());
                if (responseObject == null || responseObject.ApiVersion != exec.ApiVersion)
                {
                    throw new KubeConfigException(
                        $"external exec failed because api version {responseObject.ApiVersion} does not match {exec.ApiVersion}");
                }

                if (responseObject.Status.IsValid())
                {
                    return responseObject;
                }
                else
                {
                    throw new KubeConfigException($"external exec failed missing token or clientCertificateData field in plugin output");
                }
            }
            catch (JsonException ex)
            {
                throw new KubeConfigException($"external exec failed due to failed deserialization process: {ex}");
            }
            catch (Exception ex)
            {
                throw new KubeConfigException($"external exec failed due to uncaught exception: {ex}");
            }
        }

        internal static Process CreateRunnableExternalProcess(ExternalExecution config, bool captureStdError)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            var execInfo = new Dictionary<string, dynamic>
            {
                { "apiVersion", config.ApiVersion },
                { "kind", "ExecCredentials" },
                { "spec", new Dictionary<string, bool> { { "interactive", Environment.UserInteractive } } },
            };

            var process = new Process();

            process.StartInfo.EnvironmentVariables.Add("KUBERNETES_EXEC_INFO", JsonSerializer.Serialize(execInfo));
            if (config.EnvironmentVariables != null)
            {
                foreach (var configEnvironmentVariable in config.EnvironmentVariables)
                {
                    if (configEnvironmentVariable.ContainsKey("name") && configEnvironmentVariable.ContainsKey("value"))
                    {
                        var name = configEnvironmentVariable["name"];
                        process.StartInfo.EnvironmentVariables[name] = configEnvironmentVariable["value"];
                    }
                    else
                    {
                        var badVariable = string.Join(",", configEnvironmentVariable.Select(x => $"{x.Key}={x.Value}"));
                        throw new KubeConfigException($"Invalid environment variable defined: {badVariable}");
                    }
                }
            }

            process.StartInfo.FileName = config.Command;
            if (config.Arguments != null)
            {
                process.StartInfo.Arguments = string.Join(" ", config.Arguments);
            }

            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = captureStdError;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            return process;
        }
    }
}
