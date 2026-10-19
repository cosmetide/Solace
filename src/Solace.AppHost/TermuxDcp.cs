using System.Diagnostics;
using System.Reflection;
using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Solace.AppHost;

/// <summary>
/// Termux (proot-distro) workarounds for Aspire's DCP orchestrator.
/// </summary>
internal static class TermuxDcp
{
    // Aspire waits a fixed 20 seconds for the DCP apiserver to publish its kubeconfig
    // (KubernetesService.MaxRetryDuration). The bundled dcp binary is an ~80 MB Go
    // executable and a cold start from phone storage took ~16 seconds in testing,
    // before the package search and JIT work that run during the same startup window.
    private static readonly TimeSpan KubernetesStartupTimeout = TimeSpan.FromMinutes(5);

    // Running DCP once faults the binary into the page cache, so the instance the
    // AppHost actually uses starts almost instantly afterwards.
    private static readonly TimeSpan PrewarmTimeout = TimeSpan.FromMinutes(2);

    public static void Prepare(IServiceCollection services)
    {
        // DCP resolves "localhost" for service proxies. Android/proot services commonly
        // bind only 127.0.0.1, while Go resolves localhost to ::1 first. Without this,
        // DCP proxies and health checks fail with "dial tcp [::1]:<port>: connection
        // refused" and dependent resources (web-portal) wait forever.
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

        // KubernetesService is internal and uses a primary constructor, so resolve its
        // dependencies by hand instead of relying on ActivatorUtilities' public-only scan.
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
