using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
namespace Loomi.Services;
[Authorize]
public class StatusHub : Hub { }
