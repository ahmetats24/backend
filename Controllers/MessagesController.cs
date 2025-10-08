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
            // Son 'count' mesajı alıyoruz (default 50)
            var messages = await _db.Messages
                .Include(m => m.UserRef) // User bilgisi için
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
                .OrderBy(m => m.createdAtUtc) // eski → yeni sıralama
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

            // Resolve or create user by nickname
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

            // Save initial message bound to user
            message.UserId = user.Id;
            _db.Messages.Add(message);
            await _db.SaveChangesAsync();

            // Build AI request
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
                    // HF Inference API beklenen payload: { inputs: string }
                    var primaryUrl = string.IsNullOrWhiteSpace(aiPath) ? aiBase : new Uri(new Uri(aiBase.EndsWith('/') ? aiBase : aiBase + "/"), aiPath.TrimStart('/')).ToString();
                    Console.WriteLine($"[AI] Provider=huggingface URL={primaryUrl}");
                    response = await client.PostAsJsonAsync(primaryUrl, new { inputs = message.Text });
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Try with/without trailing slash
                        var altUrl = primaryUrl.EndsWith('/') ? primaryUrl.TrimEnd('/') : primaryUrl + "/";
                        Console.WriteLine($"[AI] Provider=huggingface ALT URL={altUrl}");
                        response = await client.PostAsJsonAsync(altUrl, new { inputs = message.Text });
                    }
                }
                else if (provider.Equals("hf-space", StringComparison.OrdinalIgnoreCase))
                {
                    var baseUri = new Uri(aiBase.EndsWith('/') ? aiBase : aiBase + "/");
                    var candidatePaths = new List<string>
                    {
                        string.IsNullOrWhiteSpace(aiPath) ? "api/predict" : aiPath.TrimStart('/'),
                        string.IsNullOrWhiteSpace(aiPath) ? "api/predict/" : (aiPath.TrimStart('/').EndsWith('/') ? aiPath.TrimStart('/') : aiPath.TrimStart('/') + "/"),
                        "run/predict",
                        "run/predict/"
                    };
                    var payloads = new object[]
                    {
                        new { data = new object[] { message.Text } },
                        new { data = new object[] { new object[] { message.Text } } },
                        new { data = new object[] { message.Text }, fn_index = 0 },
                        new { data = new object[] { new object[] { message.Text } }, fn_index = 0 }
                    };

                    HttpResponseMessage? last = null;
                    foreach (var p in candidatePaths)
                    {
                        var url = new Uri(baseUri, p);
                        foreach (var body in payloads)
                        {
                            var r = await client.PostAsJsonAsync(url, body);
                            last = r;
                            if (r.IsSuccessStatusCode)
                            {
                                response = r;
                                goto PredictOk;
                            }
                        }
                    }
                    response = last ?? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
                PredictOk:;
                }
                else
                {
                    // Custom AI service (FastAPI/Gradio) payload: { text: string }
                    var url = new Uri(new Uri(aiBase.EndsWith('/') ? aiBase : aiBase + "/"), string.IsNullOrWhiteSpace(aiPath) ? "analyze" : aiPath.TrimStart('/'));
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

            string? sentiment = null;
            double? score = null;
            if (provider.Equals("huggingface", StringComparison.OrdinalIgnoreCase))
            {
                // Read once to avoid ObjectDisposedException
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
                catch
                {
                    try
                    {
                        var hfNested = JsonSerializer.Deserialize<List<List<Dictionary<string, object>>>>(json);
                        var first = hfNested?.FirstOrDefault()?.OrderByDescending(x => double.TryParse(x.GetValueOrDefault("score")?.ToString(), out var d) ? d : 0).FirstOrDefault();
                        if (first != null)
                        {
                            sentiment = first.GetValueOrDefault("label")?.ToString();
                            if (double.TryParse(first.GetValueOrDefault("score")?.ToString(), out var dn)) score = dn;
                        }
                    }
                    catch
                    {
                        // Leave sentiment/score null; body may be an error/unsupported shape
                    }
                }
            }
            else if (provider.Equals("hf-space", StringComparison.OrdinalIgnoreCase))
            {
                // Gradio Spaces predict returns { data: [...] }
                var obj = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
                if (obj != null && obj.TryGetValue("data", out var dataObj) && dataObj is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in je.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object)
                            {
                                if (item.TryGetProperty("label", out var l)) sentiment = l.GetString();
                                if (item.TryGetProperty("score", out var sc) && sc.TryGetDouble(out var d)) score = d;
                                if (sentiment != null) break;
                            }
                            else if (item.ValueKind == JsonValueKind.String)
                            {
                                // Some Spaces return plain string label in data array
                                sentiment = item.GetString();
                                break;
                            }
                            else if (item.ValueKind == JsonValueKind.Array)
                            {
                                // Sometimes [[label, score]] or similar
                                var inner = item.EnumerateArray().ToArray();
                                if (inner.Length >= 1 && inner[0].ValueKind == JsonValueKind.String)
                                {
                                    sentiment = inner[0].GetString();
                                }
                                if (inner.Length >= 2 && inner[1].ValueKind == JsonValueKind.Number && inner[1].TryGetDouble(out var d2))
                                {
                                    score = d2;
                                }
                                if (sentiment != null) break;
                            }
                        }
                    }
                }
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

            // Update and persist sentiment
            message.Sentiment = sentiment;
            message.SentimentScore = score;
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
                .Select(m => new {
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
