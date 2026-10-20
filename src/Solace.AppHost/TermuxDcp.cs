using System.Diagnostics;
using System.Reflection;
using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Solace.AppHost;

internal static class TermuxDcp
{
    private static readonly TimeSpan KubernetesStartupTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PrewarmTimeout = TimeSpan.FromMinutes(2);

    public static void Prepare(IServiceCollection services)
    {
        Environment.SetEnvironmentVariable("DCP_IP_VERSION_PREFERENCE", "ipv4");

        ExtendKubernetesStartupTimeout(services, KubernetesStartupTimeout);
        Prewarm();
    }

    private static void ExtendKubernetesStartupTimeout(IServiceCollection services, TimeSpan timeout)
    {
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var serviceType = hostingAssembly.GetType("Aspire.Hosting.Dcp.IKubernetesService");
        var implementationType = hostingAssembly.GetType("Aspire.Hosting.Dcp.KubernetesService");

        if (serviceType is null || implementationType is null)
        {
            Console.WriteLine("[Termux] Aspire DCP service types not found; keeping the default Kubernetes startup timeout.");
            return;
        }

        var timeoutProperty = implementationType.GetProperty(
            "MaxRetryDuration",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (timeoutProperty is null || timeoutProperty.PropertyType != typeof(TimeSpan))
        {
            Console.WriteLine("[Termux] Aspire DCP MaxRetryDuration not found; keeping the default Kubernetes startup timeout.");
            return;
        }

        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != serviceType)
            {
                continue;
            }

            services[i] = ServiceDescriptor.Describe(serviceType, provider =>
            {
                var instance = CreateInstance(descriptor, provider, implementationType);
                timeoutProperty.SetValue(instance, timeout);
                return instance;
            }, descriptor.Lifetime);

            Console.WriteLine($"[Termux] DCP Kubernetes startup timeout extended to {timeout.TotalMinutes:0} minutes.");
            return;
        }

        Console.WriteLine("[Termux] Aspire DCP KubernetesService registration not found; keeping the default Kubernetes startup timeout.");
    }

    private static object CreateInstance(ServiceDescriptor descriptor, IServiceProvider provider, Type implementationType)
    {
        if (descriptor.ImplementationInstance is not null)
        {
            return descriptor.ImplementationInstance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return descriptor.ImplementationFactory(provider);
        }

        var constructor = implementationType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var arguments = constructor.GetParameters()
            .Select(parameter => provider.GetService(parameter.ParameterType))
            .ToArray();

        return constructor.Invoke(arguments);
    }

    private static void Prewarm()
    {
        try
        {
            var dcpPath = FindDcpBinary();
            if (dcpPath is null)
            {
                Console.WriteLine("[Termux] DCP binary not found; skipping pre-warm.");
                return;
            }

            var workDirectory = Path.Combine(Path.GetTempPath(), $"aspire-termux-prewarm-{Environment.ProcessId}");
            Directory.CreateDirectory(workDirectory);

            try
            {
                var kubeconfigPath = Path.Combine(workDirectory, "kubeconfig");
                var startInfo = new ProcessStartInfo(dcpPath)
                {
                    UseShellExecute = false,
                };

                startInfo.ArgumentList.Add("start-apiserver");
                startInfo.ArgumentList.Add("--server-only");
                startInfo.ArgumentList.Add("--kubeconfig");
                startInfo.ArgumentList.Add(kubeconfigPath);
                startInfo.ArgumentList.Add("-v=error");

                var stopwatch = Stopwatch.StartNew();
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    Console.WriteLine("[Termux] DCP pre-warm could not start; continuing.");
                    return;
                }

                while (!File.Exists(kubeconfigPath) && !process.HasExited && stopwatch.Elapsed < PrewarmTimeout)
                {
                    Thread.Sleep(100);
                }

                if (process.HasExited && !File.Exists(kubeconfigPath))
                {
                    Console.WriteLine($"[Termux] DCP pre-warm exited early with code {process.ExitCode}; continuing.");
                }
                else
                {
                    Console.WriteLine($"[Termux] DCP pre-warm ready after {stopwatch.Elapsed.TotalSeconds:0.0}s.");
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(workDirectory, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Termux] DCP pre-warm skipped: {ex.Message}");
        }
    }

    private static string? FindDcpBinary()
    {
        var configuredPath = Environment.GetEnvironmentVariable("ASPIRE_DCP_PATH");
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        if (File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var candidate = Path.Combine(configuredPath, "dcp");
        return File.Exists(candidate) ? candidate : null;
    }
}
