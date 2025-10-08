using ChatApp.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Api.Controllers
{
    [ApiController]
    [Route("api/users")]
    public class UsersController : ControllerBase
    {
        private readonly ChatDbContext _db;

        public UsersController(ChatDbContext db)
        {
            _db = db;
        }

        [HttpPost]
        public async Task<IActionResult> Register([FromBody] UserModel user)
        {
            if (string.IsNullOrWhiteSpace(user.Nickname))
            {
                return BadRequest("Nickname is required");
            }

            var original = user.Nickname.Trim();
            user.Nickname = original.ToLowerInvariant();
            user.DisplayName = original;
            var existing = await _db.Users.FirstOrDefaultAsync(u => u.Nickname == user.Nickname);
            if (existing != null)
            {
                // Güncelle: display name son gönderilenle eşitlensin istenirse
                existing.DisplayName = original;
                await _db.SaveChangesAsync();
                return Ok(existing);
            }

            _db.Users.Add(user);
            await _db.SaveChangesAsync();
            return Ok(user);
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById([FromRoute] int id)
        {
            var user = await _db.Users.FindAsync(id);
            return user == null ? NotFound() : Ok(user);
        }

        [HttpGet]
        public async Task<IActionResult> GetByNickname([FromQuery] string? nickname)
        {
            if (!string.IsNullOrWhiteSpace(nickname))
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Nickname == nickname.Trim().ToLowerInvariant());
                return user == null ? NotFound() : Ok(user);
            }

            var users = await _db.Users
                .OrderBy(u => u.Nickname)
                .Select(u => new { u.Id, u.Nickname, u.CreatedAtUtc })
                .ToListAsync();
            return Ok(users);
        }
    }
}


