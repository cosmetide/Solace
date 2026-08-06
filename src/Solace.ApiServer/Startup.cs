using Asp.Versioning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using System.Text;
using Solace.ApiServer.Authentication;
using Solace.ApiServer.Utils;

namespace Solace.ApiServer;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    // This method gets called by the runtime. Use this method to add services to the container.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers()
            .ConfigureApplicationPartManager(manager =>
            {
                manager.FeatureProviders.Add(new InternalControllerFeatureProvider());
            });

        services.AddResponseCompression(options =>
        {
            options.Providers.Add<GzipCompressionProvider>();
        });

        services.AddResponseCaching();

        services.AddApiVersioning(config =>
        {
            config.DefaultApiVersion = new ApiVersion(1, 1);
            config.AssumeDefaultVersionWhenUnspecified = true;
            config.ReportApiVersions = true;
        });

        services.AddAuthentication("GenoaAuth")
            .AddScheme<AuthenticationSchemeOptions, GenoaAuthenticationHandler>("GenoaAuth", null);

        services.AddDbContext<LiveDbContext>(options => options.UseSqlite(Configuration.GetConnectionString("LiveDBConnection")));
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
#pragma warning disable IDE0060 // Remove unused parameter
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
#pragma warning restore IDE0060 // Remove unused parameter
    {
        var forwardedHeadersOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };

        forwardedHeadersOptions.KnownIPNetworks.Clear();
        forwardedHeadersOptions.KnownProxies.Clear();

        app.UseForwardedHeaders(forwardedHeadersOptions);

        app.Use(async (context, next) =>
        {
            context.Items.Add("RequestStartedOn", DateTimeOffset.UtcNow);
            await next();
        });

        app.UseSerilogRequestLogging(options =>
        {
            // Customize the message template
            options.MessageTemplate = "{RemoteIpAddress} {RequestMethod} {RequestScheme}://{RequestHost}{RequestPath}{RequestQuery} responded {StatusCode} in {Elapsed:0.0000} ms";

            // Emit debug-level events instead of the defaults
            options.GetLevel = (httpContext, elapsed, ex) => LogEventLevel.Verbose;

            // Attach additional properties to the request completion event
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
                diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
                diagnosticContext.Set("RemoteIpAddress", httpContext.Connection.RemoteIpAddress);
                diagnosticContext.Set("RequestQuery", httpContext.Request.QueryString);
            };
        });

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (HttpMethods.IsPost(context.Request.Method)
                && (path.Contains("xboxlive.com", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("oauth20_token.srf", StringComparison.OrdinalIgnoreCase)))
            {
                context.Request.EnableBuffering();
                byte[] raw;
                using (var ms = new MemoryStream())
                {
                    await context.Request.Body.CopyToAsync(ms);
                    raw = ms.ToArray();
                }
                context.Request.Body.Position = 0;

                var body = Encoding.UTF8.GetString(raw);
                Log.Information("AUTH-DEBUG {Method} {Path}{Query} ContentType={ContentType} Length={Length}",
                    context.Request.Method, path, context.Request.QueryString, context.Request.ContentType, raw.Length);

                if (body.All(ch => ch >= 32 && ch != 127))
                {
                    Log.Information("AUTH-DEBUG Body: {Body}", body);
                }
                else
                {
                    Log.Information("AUTH-DEBUG Body (hex): {Hex}", Convert.ToHexString(raw));
                    Log.Information("AUTH-DEBUG Body (printable): {Body}",
                        string.Concat(body.Select(ch => ch >= 32 && ch != 127 ? ch : '?')));
                }

                await next();
                Log.Information("AUTH-DEBUG {Path} -> {StatusCode}", path, context.Response.StatusCode);
                return;
            }

            await next();
        });

        app.UseStaticFiles();
        
        //app.UseHttpsRedirection();

        app.UseRouting();

        app.UseAuthentication();
        app.UseAuthorization();

        //app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TransactionManager.MaximumTimeout });

        app.UseETagger();

        app.UseResponseCaching();
        app.UseResponseCompression();

        //app.UseSession();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
    }
}
