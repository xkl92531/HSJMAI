using HSJM.AI.Core;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.Utility.JWTHelper
{
    /// <summary>
    /// JWT加密
    /// </summary>
    public static class JWTEncryption
    {

        public static Dictionary<string, string> Decrypt(string token)
        {
            JwtSecurityTokenHandler handler = new JwtSecurityTokenHandler();
            JwtSecurityToken jwtToken = handler.ReadJwtToken(token);

            // 检查令牌是否已经过期
            if (jwtToken.ValidTo < DateTime.UtcNow)
            {
                //throw new SecurityTokenExpiredException("The token has expired.");
            }

            // 验证令牌的签名
            TokenValidationParameters validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = AppsettingsConfig.JWTConfig.Issuer,
                ValidAudience = AppsettingsConfig.JWTConfig.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AppsettingsConfig.JWTConfig.SecurityKey))
            };

            handler.ValidateToken(token, validationParameters, out SecurityToken validatedToken);

            // 确保验证的令牌是JWT
            if (validatedToken is not JwtSecurityToken)
            {
                throw new ArgumentException("The token is not a valid JWT,令牌无效");
            }

            // 解析JWT中的负载信息
            var payload = jwtToken.Claims.ToDictionary(c => c.Type, c => c.Value);

            return payload;
        }

        /// <summary>
        /// JWT加密
        /// </summary>
        /// <param name="payload"></param>
        /// <param name="expiredTime"></param>
        /// <returns></returns>
        public static string Encrypt(Dictionary<string, object> payload)
        {
            List<Claim> claims = new List<Claim>();

            foreach (var claim in payload)
            {
                claims.Add(new Claim(claim.Key, claim.Value.ToString()));

            } 
            SymmetricSecurityKey key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AppsettingsConfig.JWTConfig.SecurityKey));
            SigningCredentials creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            JwtSecurityToken token = new JwtSecurityToken(
                issuer: AppsettingsConfig.JWTConfig.Issuer,
                audience: AppsettingsConfig.JWTConfig.Audience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(AppsettingsConfig.JWTConfig.ExpiresMinutes),
                signingCredentials: creds
            );
            string resultToken = new JwtSecurityTokenHandler().WriteToken(token);
            return resultToken; 
        }
    }


}
