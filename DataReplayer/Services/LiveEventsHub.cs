using Microsoft.AspNetCore.SignalR;

namespace DataReplayer.Services;

public class LiveEventsHub : Hub
{
    // Clients just receive, they don't need to send anything to the hub for now.
}
