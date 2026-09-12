using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace NexaGrid.Controller.Hubs;

[Authorize]
public sealed class FleetHub : Hub;
