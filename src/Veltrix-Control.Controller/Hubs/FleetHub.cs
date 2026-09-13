using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace VeltrixControl.Controller.Hubs;

[Authorize]
public sealed class FleetHub : Hub;
