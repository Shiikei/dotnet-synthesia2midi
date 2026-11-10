using Microsoft.AspNetCore.SignalR;

namespace Synthesia2Midi.Hubs;

/// Un groupe SignalR par conversion (nommé d'après son Guid) pour cibler les mises à jour
/// de progression vers la bonne page ConversionDetail.razor.
public class ConversionProgressHub : Hub
{
    public async Task SubscribeToConversion(string conversionId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, conversionId);

    public async Task UnsubscribeFromConversion(string conversionId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, conversionId);
}
