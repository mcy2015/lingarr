using Microsoft.AspNetCore.Mvc;
using Lingarr.Core;
using Lingarr.Core.Models;
using Microsoft.AspNetCore.Authorization;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class VersionController : ControllerBase
{
    /// <summary>
    /// Retrieves the running version information.
    /// </summary>
    /// <returns>VersionInfo for the current build.</returns>
    /// <response code="200">Returns the version information</response>
    [AllowAnonymous]
    [HttpGet]
    public ActionResult<VersionInfo> Get()
    {
        return Ok(LingarrVersion.GetVersionInfo());
    }
}
