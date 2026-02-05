using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Siphon.Services;

namespace Siphon
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddSingleton<UserService>();
            builder.Services.AddSingleton<DownloadManager>();
            builder.Services.AddTransient<VideoDownloader>();
            builder.Services.AddSingleton<PreviewGenerator>();
            builder.Services.AddSingleton<RetentionService>();
            // Bootstrapper MUST run before TorProxyManager
            // 1. Register SystemBootstrapper as Singleton (so we can call RestartTorAsync)
            builder.Services.AddSingleton<SystemBootstrapper>();
            builder.Services.AddHostedService(p => p.GetRequiredService<SystemBootstrapper>());

            // 2. Register TorProxyManager as Singleton (so the UI can check status)
            builder.Services.AddSingleton<TorProxyManager>();
            builder.Services.AddHostedService(p => p.GetRequiredService<TorProxyManager>());
            builder.Services.AddHostedService<RetentionService>();
            builder.Services.AddHostedService<PreviewRecoveryService>();

            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.LoginPath = "/Login";
                    options.ExpireTimeSpan = TimeSpan.FromHours(24); // Req 2: 24 Hour Expiry
                    options.SlidingExpiration = true; // Refresh cookie if active
                    options.AccessDeniedPath = "/Login";
                });

            builder.Services.AddRazorPages(options =>
            {
                // Req 3: Protect everything by default
                options.Conventions.AuthorizeFolder("/");
                // Allow anonymous access to Login and Setup so you don't get a redirect loop
                options.Conventions.AllowAnonymousToPage("/Login");
                options.Conventions.AllowAnonymousToPage("/Setup");
            });

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.Use(async (context, next) =>
            {
                var userService = context.RequestServices.GetRequiredService<UserService>();

                // If config missing, and we aren't already on the Setup page, go to Setup
                if (userService.IsSetupRequired() && !context.Request.Path.StartsWithSegments("/Setup"))
                {
                    context.Response.Redirect("/Setup");
                    return;
                }

                // If config exists, but user tries to access Setup, block them (redirect to Login/Home)
                if (!userService.IsSetupRequired() && context.Request.Path.StartsWithSegments("/Setup"))
                {
                    context.Response.Redirect("/Login");
                    return;
                }

                await next();
            });

            app.MapStaticAssets();
            app.MapRazorPages()
               .WithStaticAssets();

            app.Run();
        }
    }

    public static class JsonHandler
    {
        public static void SerializeJsonFile<T>(string filePath, T obj, bool append = false)
        {
            using var writer = new StreamWriter(filePath, append);
            writer.Write(JsonConvert.SerializeObject(obj));
        }

        public static T DeserializeJsonFile<T>(string filePath) where T : new()
        {
            if (!System.IO.File.Exists(filePath))
                return new T();

            using var reader = new StreamReader(filePath);
            return JsonConvert.DeserializeObject<T>(reader.ReadToEnd());
        }
    }
}
