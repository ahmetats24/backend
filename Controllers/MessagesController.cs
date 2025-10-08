using Microsoft.AspNetCore.Mvc;
using ChatApp.Api.Models;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ChatApp.Api.Controllers
{
    [ApiController]
    [Route("api/messages")]
    public class MessagesController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ChatDbContext _db;
        private readonly IConfiguration _configuration;

        public MessagesController(IHttpClientFactory httpClientFactory, ChatDbContext db, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _db = db;
            _configuration = configuration;
        }

        // GET /api/messages/all
        [HttpGet("all")]
        public async Task<IActionResult> GetAllMessages([FromQuery] int count = 50)
        {
            var messages = await _db.Messages
                .Include(m => m.UserRef)
                .OrderByDescending(m => m.Id)
                .Take(Math.Clamp(count, 1, 100))
                .Select(m => new
                {
                    id = m.Id,
                    text = m.Text,
                    user = m.UserRef != null ? m.UserRef.DisplayName : m.User,
                    sentiment = m.Sentiment,
                    score = m.SentimentScore,
                    createdAtUtc = m.CreatedAtUtc
                })
                .OrderBy(m => m.createdAtUtc)
                .ToListAsync();

            return Ok(messages);
        }

        [HttpPost]
        public async Task<IActionResult> PostMessage([FromBody] MessageModel message)
        {
            if (string.IsNullOrWhiteSpace(message.User) || string.IsNullOrWhiteSpace(message.Text))
            {
                return BadRequest("User and Text are required.");
            }

            // Kullanıcıyı bul veya oluştur
            var originalNickname = message.User.Trim();
            var nickname = originalNickname.ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Nickname == nickname);
            if (user == null)
            {
                user = new UserModel { Nickname = nickname, DisplayName = originalNickname };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();
            }
            else if (user.DisplayName != originalNickname)
            {
                user.DisplayName = originalNickname;
                await _db.SaveChangesAsync();
            }

            // Mesaj kaydet
            message.UserId = user.Id;
            _db.Messages.Add(message);
            await _db.SaveChangesAsync();

            // AI request
            var client = _httpClientFactory.CreateClient();
            var provider = _configuration["AiService:Provider"] ?? "huggingface";
            var aiBase = _configuration["AiService:BaseUrl"] ?? "";
            var aiPath = _configuration["AiService:AnalyzePath"] ?? string.Empty;
            var hfToken = _configuration["AiService:HuggingFaceToken"];
            if (!string.IsNullOrEmpty(hfToken))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", hfToken);
            }

            HttpResponseMessage response;
            try
            {
                if (provider.Equals("huggingface", StringComparison.OrdinalIgnoreCase))
                {
                    var primaryUrl = string.IsNullOrWhiteSpace(aiPath)
                        ? aiBase
                        : new Uri(new Uri(aiBase.EndsWith('/') ? aiBase : aiBase + "/"), aiPath.TrimStart('/')).ToString();

                    response = await client.PostAsJsonAsync(primaryUrl, new { inputs = message.Text });

                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        var altUrl = primaryUrl.EndsWith('/') ? primaryUrl.TrimEnd('/') : primaryUrl + "/";
                        response = await client.PostAsJsonAsync(altUrl, new { inputs = message.Text });
                    }
                }
                else
                {
                    // Custom AI service
                    var url = new Uri(new Uri(aiBase.EndsWith('/') ? aiBase : aiBase + "/"),
                        string.IsNullOrWhiteSpace(aiPath) ? "predict" : aiPath.TrimStart('/'));
                    response = await client.PostAsJsonAsync(url, new { text = message.Text });
                }
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { id = message.Id, text = message.Text, error = "AI connection failed", details = ex.Message });
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                return StatusCode((int)response.StatusCode, new { id = message.Id, text = message.Text, error = "AI request failed", details = errorBody });
            }

            // AI sonucunu çöz
            string? sentiment = null;
            double? score = null;

            if (provider.Equals("huggingface", StringComparison.OrdinalIgnoreCase))
            {
                var json = await response.Content.ReadAsStringAsync();
                try
                {
                    var hfSingle = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(json);
                    var best = hfSingle?
                        .OrderByDescending(x => double.TryParse(x.GetValueOrDefault("score")?.ToString(), out var d) ? d : 0)
                        .FirstOrDefault();
                    if (best != null)
                    {
                        sentiment = best.GetValueOrDefault("label")?.ToString();
                        if (double.TryParse(best.GetValueOrDefault("score")?.ToString(), out var ds)) score = ds;
                    }
                }
                catch { }
            }
            else
            {
                var result = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
                sentiment = result?.GetValueOrDefault("label")?.ToString();
                if (result != null && result.TryGetValue("score", out var s) && double.TryParse(s?.ToString(), out var d))
                {
                    score = d;
                }
            }

            // DB kaydını güncelle
            message.Sentiment = sentiment;
            message.SentimentScore = score;
            _db.Messages.Update(message);
            await _db.SaveChangesAsync();

            return Ok(new { id = message.Id, text = message.Text, sentiment, score });
        }

        [HttpGet]
        public async Task<IActionResult> GetMessagesByUser([FromQuery] string nickname, [FromQuery] int count = 20)
        {
            if (string.IsNullOrWhiteSpace(nickname)) return BadRequest("nickname is required");
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Nickname == nickname.Trim().ToLowerInvariant());
            if (user == null) return Ok(Array.Empty<MessageModel>());

            var messages = await _db.Messages
                .Where(m => m.UserId == user.Id)
                .OrderByDescending(m => m.Id)
                .Take(Math.Clamp(count, 1, 100))
                .Select(m => new
                {
                    id = m.Id,
                    text = m.Text,
                    sentiment = m.Sentiment,
                    score = m.SentimentScore,
                    createdAtUtc = m.CreatedAtUtc
                })
                .ToListAsync();

            return Ok(messages);
        }
    }
}
