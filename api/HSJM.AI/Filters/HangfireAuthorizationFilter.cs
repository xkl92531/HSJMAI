using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Linq;
using System.Net;

namespace HSJM.AI.Filters
{
    /// <summary>
    /// Hangfire Dashboard 授权过滤器
    /// </summary>
    public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
    {
        private const string TokenCookieName = "HSJM_HANGFIRE_TOKEN";

        public bool Authorize(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();
            
            // 开发环境允许所有访问
            if (httpContext.RequestServices.GetService<IHostEnvironment>()?.IsDevelopment() == true)
            {
                return true;
            }

            var configuration = httpContext.RequestServices.GetService<IConfiguration>();
            var dashboardSection = configuration?.GetSection("WebConfig:HangfireDashboard");

            // 内网发布场景可通过配置放行，避免 Hangfire 页面无法携带前端 JWT 而返回 401。
            if (dashboardSection?.GetValue<bool>("AllowAnonymous") == true)
            {
                return true;
            }

            if (IsTokenAuthorized(httpContext, dashboardSection))
            {
                return true;
            }

            if (IsIpAllowed(httpContext, dashboardSection))
            {
                return true;
            }

            // 仍兼容手动携带 Authorization Bearer 的访问方式
            return httpContext.User.Identity?.IsAuthenticated ?? false;
        }

        private static bool IsTokenAuthorized(HttpContext httpContext, IConfigurationSection? dashboardSection)
        {
            var expectedToken = dashboardSection?["AccessToken"];
            if (string.IsNullOrWhiteSpace(expectedToken))
            {
                return false;
            }

            var request = httpContext.Request;
            var suppliedToken =
                request.Query["access_token"].FirstOrDefault()
                ?? request.Query["token"].FirstOrDefault()
                ?? request.Headers["X-Hangfire-Token"].FirstOrDefault()
                ?? request.Cookies[TokenCookieName];

            if (!string.Equals(suppliedToken, expectedToken, StringComparison.Ordinal))
            {
                return false;
            }

            if (request.Query.ContainsKey("access_token") || request.Query.ContainsKey("token"))
            {
                httpContext.Response.Cookies.Append(TokenCookieName, expectedToken, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = request.IsHttps,
                    Path = "/hangfire",
                    Expires = DateTimeOffset.UtcNow.AddHours(8)
                });
            }

            return true;
        }

        private static bool IsIpAllowed(HttpContext httpContext, IConfigurationSection? dashboardSection)
        {
            var allowedIps = dashboardSection?.GetSection("AllowedIpAddresses").Get<string[]>();
            if (allowedIps == null || allowedIps.Length == 0)
            {
                return false;
            }

            if (allowedIps.Any(x => x == "*"))
            {
                return true;
            }

            var remoteIp = httpContext.Connection.RemoteIpAddress;
            var remoteIpText = NormalizeIp(remoteIp);
            var forwardedIpText = httpContext.Request.Headers["X-Forwarded-For"]
                .FirstOrDefault()?
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Trim();

            return allowedIps.Any(ip =>
                string.Equals(ip, remoteIpText, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ip, forwardedIpText, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(ip, "localhost", StringComparison.OrdinalIgnoreCase)
                    && remoteIp != null
                    && IPAddress.IsLoopback(remoteIp)));
        }

        private static string? NormalizeIp(IPAddress? ip)
        {
            if (ip == null)
            {
                return null;
            }

            return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
        }
    }
}
