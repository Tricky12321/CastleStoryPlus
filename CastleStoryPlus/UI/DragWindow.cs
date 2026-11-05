using UnityEngine;
using UnityEngine.EventSystems;

namespace CastleStoryPlus.UI;

// A window the player moves with the mouse: dragged with the left button from anywhere on it that is not itself
// dragged (a scroll list, a slider). It stays where it is put while the game runs (the window is kept, only hidden)
// and is kept on screen. UiKit.Draggable adds it.
internal class DragWindow : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
	private RectTransform _rect;

	private Canvas _canvas;

	private bool _dragging;

	private void Awake()
	{
		_rect = (RectTransform)transform;
	}

	public void OnBeginDrag(PointerEventData eventData)
	{
		_canvas = GetComponentInParent<Canvas>();
		_canvas = (_canvas != null) ? _canvas.rootCanvas : null;
		_dragging = eventData.button == PointerEventData.InputButton.Left && _canvas != null;
	}

	public void OnDrag(PointerEventData eventData)
	{
		if (!_dragging)
		{
			return;
		}
		_rect.anchoredPosition += eventData.delta / _canvas.scaleFactor;
		KeepOnScreen();
	}

	public void OnEndDrag(PointerEventData eventData)
	{
		_dragging = false;
	}

	// Moved back inside the canvas; a window taller than the screen keeps its top on it.
	private void KeepOnScreen()
	{
		RectTransform canvas = (RectTransform)_canvas.transform;
		Vector3[] corners = new Vector3[4];
		_rect.GetWorldCorners(corners);
		Vector2 min = canvas.InverseTransformPoint(corners[0]);
		Vector2 max = canvas.InverseTransformPoint(corners[2]);
		Rect area = canvas.rect;
		Vector2 shift = Vector2.zero;
		if (min.x < area.xMin)
		{
			shift.x = area.xMin - min.x;
		}
		else if (max.x > area.xMax)
		{
			shift.x = area.xMax - max.x;
		}
		if (max.y > area.yMax)
		{
			shift.y = area.yMax - max.y;
		}
		else if (min.y < area.yMin)
		{
			shift.y = Mathf.Min(area.yMin - min.y, area.yMax - max.y);
		}
		if (shift != Vector2.zero)
		{
			_rect.anchoredPosition += shift;
		}
	}
}
