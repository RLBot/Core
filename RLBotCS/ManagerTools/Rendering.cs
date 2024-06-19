using rlbot.flat;
using RLBotCS.Conversion;
using Bridge.Controller;
using Bridge.TCP;

namespace RLBotCS.ManagerTools;

public class Rendering(TcpMessenger tcpMessenger)
{
    private readonly RenderingSender _renderingSender = new(tcpMessenger);
    private readonly Dictionary<int, Dictionary<int, List<ushort>>> _clientRenderTracker = [];

    private ushort? RenderItem(RenderTypeUnion renderItem) =>
        renderItem.Value switch
        {
            Line3DT { Start: var start, End: var end, Color: var color }
                => _renderingSender.AddLine3D(
                    FlatToModel.ToVectorFromT(start),
                    FlatToModel.ToVectorFromT(end),
                    FlatToModel.ToColor(color)
                ),
            PolyLine3DT { Points: var points, Color: var color }
                => _renderingSender.AddLine3DSeries(
                    points.Select(FlatToModel.ToVectorFromT).ToList(),
                    FlatToModel.ToColor(color)
                ),
            String2DT
                {
                    Text: var text,
                    X: var x,
                    Y: var y,
                    Foreground: var foreground,
                    Background: var background,
                    HAlign: var hAlign,
                    VAlign: var vAlign,
                    Scale: var scale
                }
                => _renderingSender.AddText2D(
                    text,
                    x,
                    y,
                    FlatToModel.ToColor(foreground),
                    FlatToModel.ToColor(background),
                    (byte)hAlign,
                    (byte)vAlign,
                    scale
                ),
            String3DT
                {
                    Text: var text,
                    Position: var position,
                    Foreground: var foreground,
                    Background: var background,
                    HAlign: var hAlign,
                    VAlign: var vAlign,
                    Scale: var scale
                }
                => _renderingSender.AddText3D(
                    text,
                    FlatToModel.ToVectorFromT(position),
                    FlatToModel.ToColor(foreground),
                    FlatToModel.ToColor(background),
                    (byte)hAlign,
                    (byte)vAlign,
                    scale
                ),
            _ => null
        };

    public void AddRenderGroup(int clientId, int renderId, List<RenderMessageT> renderItems)
    {
        var clientRenders = _clientRenderTracker.GetValueOrDefault(clientId, []);
        // Clear the previous render group
        if (clientRenders.TryGetValue(renderId, out var previousRenderGroup))
            foreach (ushort renderItem in previousRenderGroup)
                _renderingSender.RemoveRenderItem(renderItem);

        List<ushort> renderGroup = [];
        foreach (RenderMessageT renderItem in renderItems)
            if (RenderItem(renderItem.Variety) is { } renderItemId)
                renderGroup.Add(renderItemId);

        _renderingSender.Send();

        // Add to the tracker
        clientRenders[renderId] = renderGroup;
        _clientRenderTracker[clientId] = clientRenders;
    }

    public void RemoveRenderGroup(int clientId, int renderId)
    {
        if (!_clientRenderTracker.TryGetValue(clientId, out var clientRenders))
            return;

        if (!clientRenders.TryGetValue(renderId, out var renderItems))
            return;

        foreach (ushort renderItem in renderItems)
            _renderingSender.RemoveRenderItem(renderItem);

        // Remove the renderId from the client
        clientRenders.Remove(renderId);
        _renderingSender.Send();
    }

    public void ClearClientRenders(int clientId)
    {
        if (!_clientRenderTracker.TryGetValue(clientId, out var clientRenders))
            return;

        // Tell the game to remove all the renders
        foreach (int renderId in clientRenders.Keys)
        foreach (ushort renderItem in clientRenders[renderId])
            _renderingSender.RemoveRenderItem(renderItem);

        // Remove the client from the tracker
        _clientRenderTracker.Remove(clientId);
        _renderingSender.Send();
        Console.WriteLine($"Client {clientId} renders cleared");
    }

    public void ClearAllRenders()
    {
        // Tell the game to remove all the renders
        // TODO: We might not be able to remove all the renders in one tick
        foreach (var clientRenders in _clientRenderTracker.Values)
        foreach (int renderId in clientRenders.Keys)
        foreach (ushort renderItem in clientRenders[renderId])
            _renderingSender.RemoveRenderItem(renderItem);

        // Clear the tracker
        _clientRenderTracker.Clear();
        _renderingSender.Send();

        Console.WriteLine("All renders cleared");
    }
}
