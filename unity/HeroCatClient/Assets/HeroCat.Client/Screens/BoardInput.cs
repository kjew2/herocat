using HeroCat.Client.Core;
using HeroCat.Sim.Content;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// Fish Board touch input (07 section 16): one handler on the grid turns pointer events into FishBoardVm calls with
    /// grid-local reference-pixel positions; the vm's state machine decides drag versus tap, long press and the command.
    /// </summary>
    public sealed class BoardInput : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IInitializePotentialDragHandler, IDragHandler
    {
        MainScreen _main;
        RectTransform _grid;

        public void Init(MainScreen main, RectTransform grid)
        {
            _main = main;
            _grid = grid;
        }

        Vector2 Local(PointerEventData e)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_grid, e.position, e.pressEventCamera, out var p);
            var r = _grid.rect;
            return new Vector2(p.x - r.xMin, r.yMax - p.y);   // origin top-left, y down
        }

        public void OnInitializePotentialDrag(PointerEventData e) => e.useDragThreshold = false;   // the vm applies UI_DRAG_START_PX

        public void OnPointerDown(PointerEventData e)
        {
            var p = Local(e);
            _main.BoardVm.PointerDown(e.pointerId, _main.CellAt(p), p.x, p.y, _main.Now);
        }

        public void OnDrag(PointerEventData e)
        {
            var p = Local(e);
            _main.BoardVm.PointerMove(e.pointerId, _main.CellAt(p), p.x, p.y, _main.Now);
            _main.MoveGhost(p);
        }

        public void OnPointerUp(PointerEventData e)
        {
            var p = Local(e);
            _main.BoardVm.PointerUp(e.pointerId, _main.CellAt(p), p.x, p.y, _main.Now);
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) _main?.BoardVm.Cancel();
        }
    }
}
