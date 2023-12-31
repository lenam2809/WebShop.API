using EPS.API.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using EPS.API.Helpers;
using EPS.Data.Entities;
using EPS.Service;

namespace EPS.API.Controllers
{
    [Route("api/token")]
    public class TokenController : BaseController
    {
        private Data.EPSContext _context;
        //some config in the appsettings.json
        private IOptions<Audience> _settings;
        //repository to handler the sqlite database
        private IConfiguration _configuration;
        public TokenController(IOptions<Audience> settings, Data.EPSContext context, IConfiguration configuration)
        {
            this._settings = settings;
            _context = context;
            _configuration = configuration;
            //_SolrLogServices = new SolrServices<SolrLogs>(_solrModel);
        }
        //private async Task AddLogLoginAsync(string CreatedBy, int CreatedByID, SolrServices<SolrLogs> _SolrLogServices, string Content, DOITUONG Object, ActionLogs Action, StatusLogs Status, int objectID = 0)
        //{
        //    //string CreatedBy = "Người dùng ẩn danh";
        //    //int CreatedByID = 0;
        //    string RemoteIpAddress = HttpContext.Connection.RemoteIpAddress.ToString();
        //    await _SolrLogServices.AddItemAsync(new SolrLogs(Content, Object, RemoteIpAddress,(int) Action, (int)Status, CreatedBy, CreatedByID, objectID));
        //}
        [HttpPost("auth")]
        public async Task<IActionResult> Auth(TokenRequestParams parameters)
        {
           
            // Verify client's identification
            var client = await _context.IdentityClients.SingleOrDefaultAsync(x => x.IdentityClientId.Equals(parameters.client_id) && x.SecretKey.Equals(parameters.client_secret));

            if (client == null)
            {
                return BadRequest("Unauthorized client.");
            }           

            if (parameters.grant_type == "password")
            {
                //thêm vào thống kê lượt truy cập
                return await DoPassword(parameters, client);
            }
            else if (parameters.grant_type == "refresh_token")
            {
                return await DoRefreshToken(parameters, client);
            }
            else if (parameters.grant_type == "invalidate_token")
            {
                return await DoInvalidateToken(parameters);
            }
            else
            {
                return BadRequest("Invalid grant type.");
            }
        }

        private async Task<IActionResult> DoInvalidateToken(TokenRequestParams parameters)
        {
            var token = await _context.IdentityRefreshTokens.FirstOrDefaultAsync(x => x.RefreshToken == parameters.refresh_token);

            if (token == null)
            {
                return Ok();
            }

            _context.Remove(token);

            await _context.SaveChangesAsync();

            return Ok();
        }
        private async Task<IActionResult> DoPassword(TokenRequestParams parameters, IdentityClient client)
        {
            //validate the client_id/client_secret/username/password                                          
            var user = await _context.Users.FirstOrDefaultAsync(x => x.UserName == parameters.username && x.Status == 2 && !x.DeletedDate.HasValue);



            if (user == null || user.DeletedDate.HasValue)
            {
                //await AddLogLoginAsync("Người dùng ẩn danh", 0, _SolrLogServices, "Đăng nhập hệ thống: với tài khoản " + parameters.username, DOITUONG.Login, ActionLogs.Login, StatusLogs.Error);
                return BadRequest("Invalid user infomation.");
            }

            var passwordHasher = new PasswordHasher<User>();
            var passwordVerificationResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, parameters.password);

            if (passwordVerificationResult == PasswordVerificationResult.Failed)
            {
               // await AddLogLoginAsync("Người dùng ẩn danh", 0, _SolrLogServices, "Đăng nhập hệ thống: với tài khoản " + parameters.username, DOITUONG.Login, ActionLogs.Login, StatusLogs.Error);
                return BadRequest("Invalid user infomation.");
            }

            var refresh_token = Guid.NewGuid().ToString().Replace("-", "");

            var rToken = new IdentityRefreshToken
            {
                ClientId = parameters.client_id,
                RefreshToken = refresh_token,
                IdentityRefreshTokenId = Guid.NewGuid().ToString(),
                IssuedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddDays(client.RefreshTokenLifetime),
                Identity = parameters.username,
            };

            //store the refresh_token
            _context.IdentityRefreshTokens.Add(rToken);

            await _context.SaveChangesAsync();
            //lưu vào log solr
            var returnvalue = GetJwt(parameters.client_id, refresh_token, user, parameters.username);
            //await AddLogLoginAsync(parameters.username, user.Id, _SolrLogServices, "Đăng nhập hệ thống bằng tài khoản: " + parameters.username, DOITUONG.Login, ActionLogs.Login, StatusLogs.Success);

            return Ok(returnvalue);
        }
       

        //scenario 2 ： get the access_token by refresh_token
        private async Task<IActionResult> DoRefreshToken(TokenRequestParams parameters, IdentityClient client)
        {
            var token = await _context.IdentityRefreshTokens.FirstOrDefaultAsync(x => x.RefreshToken == parameters.refresh_token);

            if (token == null)
            {
                return BadRequest("Token not found.");
            }

            if (token.IsExpired)
            {
                // Remove refresh token if expired
                _context.IdentityRefreshTokens.Remove(token);
                await _context.SaveChangesAsync();

                return BadRequest("Token has expired.");
            }

            var refresh_token = Guid.NewGuid().ToString().Replace("-", "");

            //remove the old refresh_token and add a new refresh_token
            _context.IdentityRefreshTokens.Remove(token);

            _context.IdentityRefreshTokens.Add(new IdentityRefreshToken
            {
                ClientId = parameters.client_id,
                RefreshToken = refresh_token,
                IdentityRefreshTokenId = Guid.NewGuid().ToString(),
                IssuedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddDays(client.RefreshTokenLifetime),
                Identity = token.Identity
            });

            await _context.SaveChangesAsync();

            var user = await _context.Users.SingleOrDefaultAsync(x => x.UserName == token.Identity);

            if (user == null)
            {
                return BadRequest("User not found.");
            }

            return Ok(GetJwt(parameters.client_id, refresh_token, user, token.Identity));
        }

        private string GetJwt(string client_id, string refresh_token, User user, string userName)
        {
            var now = DateTime.UtcNow;
            //Lấy quyền từ bảng trung gian
            var lstRoles = (from a in _context.GroupRolePermission
                            join c in _context.GroupUser on a.GroupId equals c.GroupId
                            join d in _context.Users on c.UserId equals d.Id
                            where d.UserName == userName && a.Value == 1
                            select a.Role.Name + '-' + a.Permission.Code).ToList();

            //var PermissionUser = (from a in _context.GroupUser
            //                      join b in _context.Users on a.UserId equals b.Id
            //                      where b.UserName == userName
            //                      select a.GroupId).ToList();
            //var PermissionUserCode = (from a in _context.GroupUser
            //                          join b in _context.Users on a.UserId equals b.Id
            //                          where b.UserName == userName
            //                          select a.GroupId).ToList();

            var oUserInfo = _context.UserDetail.FirstOrDefault(x => x.UserId == user.Id);            

            var Avatar = !string.IsNullOrEmpty(oUserInfo.Avatar) ? oUserInfo.Avatar : "";

            var claims = new Claim[]
           {
                new Claim(CustomClaimTypes.ClientId, client_id),
                new Claim(CustomClaimTypes.UserId, user.Id.ToString()),
                new Claim(ClaimTypes.GivenName, user.FullName, ClaimValueTypes.String),
                new Claim(ClaimTypes.NameIdentifier, user.UserName, ClaimValueTypes.String),
                new Claim(CustomClaimTypes.Privileges, string.Join(",", lstRoles)),
                //new Claim(CustomClaimTypes.UnitId, string.Join(",", PermissionUser)),
                //new Claim(CustomClaimTypes.UnitCode, string.Join(",", PermissionUserCode)),
                new Claim(CustomClaimTypes.TaiKhoanID, user.Id.ToString()),
                new Claim(CustomClaimTypes.AnhDaiDien, Avatar),
                new Claim(CustomClaimTypes.IsAdministrator, user.IsAdministrator?"true":"false")
           };


            var symmetricKeyAsBase64 = _settings.Value.Secret;
            var keyByteArray = Encoding.ASCII.GetBytes(symmetricKeyAsBase64);
            var signingKey = new SymmetricSecurityKey(keyByteArray);
            var expires = now.Add(TimeSpan.FromMinutes(_configuration.GetValue<double>("TimeSpanToken")));

            var jwt = new JwtSecurityToken(
                issuer: _settings.Value.ValidIssuer,
                audience: _settings.Value.ValidAudience,
                claims: claims,
                notBefore: now,
                expires: expires,
                signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
            var encodedJwt = new JwtSecurityTokenHandler().WriteToken(jwt);

            var response = new
            {
                access_token = encodedJwt,
                expires,
                refresh_token,
                fullName = user.FullName,
                username = user.UserName,
                idTaiKhoan = user.Id,
                anhdaidien = Avatar,
                isAdministrator = user.IsAdministrator,
                lstRoles,
            };

            return JsonConvert.SerializeObject(response, new JsonSerializerSettings { Formatting = Formatting.Indented });
        }
    }
}
