using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UIElements;

public partial class CoachBehaviour
{
    bool nativeDragging, nativeMouseDrag;
    Vector2 nativeDragStart;
    int nativeDragPointer, nativeDragMoves;
    EventCallback<PointerDownEvent> nativeHeaderDown;
    EventCallback<PointerMoveEvent> nativeHeaderMove;
    EventCallback<PointerUpEvent> nativeHeaderUp;
    EventCallback<PointerCaptureOutEvent> nativeHeaderCaptureOut;
    EventCallback<MouseDownEvent> nativeHeaderMouseDown;
    EventCallback<MouseMoveEvent> nativeHeaderMouseMove;
    EventCallback<MouseUpEvent> nativeHeaderMouseUp;

    bool BeginHeaderDrag(Vector2 point, int button, int pointer, bool mouse)
    {
        bool hit = Hit(header, point);
        if (ProbeEnabled) CoachPlugin.L.LogInfo($"Panel {(mouse ? "mouse" : "pointer")} down button={button} at({point.x:0},{point.y:0}) header={header.worldBound} hit={hit}");
        if (nativeDragging || button != 0 || !hit || Hit(minBtn, point) || Hit(maxBtn, point) || Hit(closeBtn, point)) return false;
        nativeDragging = true; nativeMouseDrag = mouse; dragging = false; userMoved = true;
        nativeDragPointer = pointer; nativeDragStart = point; nativeDragMoves = 0; dragL = posLeft; dragT = posTop;
        CoachPlugin.L.LogInfo("Panel native drag started via " + (mouse ? "mouse" : "pointer"));
        try { PointerCaptureHelper.CapturePointer(panelRoot.Cast<IEventHandler>(), pointer); }
        catch (Exception ex) { LogOnce("PanelDragCapture", ex); }
        return true;
    }

    void MoveHeaderDrag(Vector2 point)
    {
        posLeft = dragL + point.x - nativeDragStart.x; posTop = dragT + point.y - nativeDragStart.y;
        if (nativeDragMoves++ == 0) CoachPlugin.L.LogInfo($"Panel native drag first move({point.x:0},{point.y:0})");
    }

    void EndHeaderDrag()
    {
        nativeDragging = false; dragging = false;
        try { PointerCaptureHelper.ReleasePointer(panelRoot.Cast<IEventHandler>(), nativeDragPointer); }
        catch (Exception ex) { LogOnce("PanelDragRelease", ex); }
        CoachPlugin.L.LogInfo($"Panel native drag ended({posLeft:0},{posTop:0}); moves={nativeDragMoves}");
    }

    void WireHeaderDrag()
    {
        header.pickingMode = PickingMode.Position;
        nativeHeaderDown = DelegateSupport.ConvertDelegate<EventCallback<PointerDownEvent>>(new Action<PointerDownEvent>(evt =>
        {
            if (BeginHeaderDrag(new Vector2(evt.position.x, evt.position.y), evt.button, evt.pointerId, false)) evt.StopPropagation();
        }));
        nativeHeaderMove = DelegateSupport.ConvertDelegate<EventCallback<PointerMoveEvent>>(new Action<PointerMoveEvent>(evt =>
        {
            if (!nativeDragging || nativeMouseDrag || evt.pointerId != nativeDragPointer) return;
            MoveHeaderDrag(new Vector2(evt.position.x, evt.position.y)); evt.StopPropagation();
        }));
        nativeHeaderUp = DelegateSupport.ConvertDelegate<EventCallback<PointerUpEvent>>(new Action<PointerUpEvent>(evt =>
        {
            if (!nativeDragging || nativeMouseDrag || evt.pointerId != nativeDragPointer) return;
            EndHeaderDrag(); evt.StopPropagation();
        }));
        nativeHeaderCaptureOut = DelegateSupport.ConvertDelegate<EventCallback<PointerCaptureOutEvent>>(new Action<PointerCaptureOutEvent>(evt =>
        {
            if (evt.pointerId == nativeDragPointer && nativeDragging)
            {
                CoachPlugin.L.LogInfo("Panel native drag capture lost; moves=" + nativeDragMoves);
                nativeDragging = false; dragging = false;
            }
        }));
        nativeHeaderMouseDown = DelegateSupport.ConvertDelegate<EventCallback<MouseDownEvent>>(new Action<MouseDownEvent>(evt =>
        {
            if (BeginHeaderDrag(evt.mousePosition, evt.button, PointerId.mousePointerId, true)) evt.StopPropagation();
        }));
        nativeHeaderMouseMove = DelegateSupport.ConvertDelegate<EventCallback<MouseMoveEvent>>(new Action<MouseMoveEvent>(evt =>
        {
            if (!nativeDragging || !nativeMouseDrag) return;
            MoveHeaderDrag(evt.mousePosition); evt.StopPropagation();
        }));
        nativeHeaderMouseUp = DelegateSupport.ConvertDelegate<EventCallback<MouseUpEvent>>(new Action<MouseUpEvent>(evt =>
        {
            if (!nativeDragging || !nativeMouseDrag || evt.button != 0) return;
            EndHeaderDrag(); evt.StopPropagation();
        }));
        // Capture at the panel before child controls can consume events; start only on
        // the header, excluding its controls. Mouse compatibility covers legacy UI routes.
        panelRoot.RegisterCallback<PointerDownEvent>(nativeHeaderDown, TrickleDown.TrickleDown);
        panelRoot.RegisterCallback<PointerMoveEvent>(nativeHeaderMove, TrickleDown.TrickleDown);
        panelRoot.RegisterCallback<PointerUpEvent>(nativeHeaderUp, TrickleDown.TrickleDown);
        panelRoot.RegisterCallback<PointerCaptureOutEvent>(nativeHeaderCaptureOut, TrickleDown.NoTrickleDown);
        panelRoot.RegisterCallback<MouseDownEvent>(nativeHeaderMouseDown, TrickleDown.TrickleDown);
        panelRoot.RegisterCallback<MouseMoveEvent>(nativeHeaderMouseMove, TrickleDown.TrickleDown);
        panelRoot.RegisterCallback<MouseUpEvent>(nativeHeaderMouseUp, TrickleDown.TrickleDown);
    }
}
