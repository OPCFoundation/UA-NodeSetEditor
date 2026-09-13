using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodeSetEditor.Server.Model;

namespace NodeSetEditor.Server.Controllers
{
    [ApiController]
    [ApiExplorerSettings(IgnoreApi = true)] // Not part of the published API surface.
    [Route("[controller]")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [AllowAnonymous] // Stub endpoint; returns no user-specific data.
    public class AccountController : ControllerBase
    {
        [HttpGet()]
        public Account Get(int id)
        {
            return new Account() { Id = id, Name = "Anonymous" };
        }
    }
}
