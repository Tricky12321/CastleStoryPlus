using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CastleStoryPlus.GameUi;

// A window the player moves with the mouse: dragged with the left button from anywhere on it that is not itself
// dragged (a scroll list, a slider). The place it is moved to stays, per window, until the game is left; a double
// click on the window puts it back where the menu places it. Kept on screen.
internal class CsWindowDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
{
	// Moved windows' places (bottom left corner in the parent), by window.
	private static readonly Dictionary<string, Vector2> Placed = new Dictionary<string, Vector2>();

	private RectTransform _window;

	private string _key;

	// The pointer's distance from the window's corner when the drag began.
	private Vector2 _grab;

	private bool _dragging;

	internal static CsWindowDrag Add(RectTransform window, string key)
	{
		CsWindowDrag drag = window.gameObject.AddComponent<CsWindowDrag>();
		drag._window = window;
		drag._key = key;
		return drag;
	}

	// Forget every moved place (a new game).
	internal static void Reset()
	{
		Placed.Clear();
	}

	// The window's moved place, kept on screen; false while the menu places it.
	internal bool TryPlace(out Vector2 position)
	{
		if (!Placed.TryGetValue(_key, out position))
		{
			return false;
		}
		position = Clamp(position);
		return true;
	}

	public void OnBeginDrag(PointerEventData eventData)
	{
		_dragging = false;
		if (eventData.button == PointerEventData.InputButton.Left && Local(eventData, out Vector2 local))
		{
			_grab = _window.anchoredPosition - local;
			_dragging = true;
		}
	}

	public void OnDrag(PointerEventData eventData)
	{
		if (!_dragging || !Local(eventData, out Vector2 local))
		{
			return;
		}
		Vector2 position = Clamp(local + _grab);
		Placed[_key] = position;
		_window.anchoredPosition = position;
	}

	public void OnEndDrag(PointerEventData eventData)
	{
		_dragging = false;
	}

	public void OnPointerClick(PointerEventData eventData)
	{
		if (eventData.button == PointerEventData.InputButton.Left && eventData.clickCount == 2)
		{
			Placed.Remove(_key);
		}
	}

	// The pointer in the parent, from its lower left corner (where the window's anchor is).
	private bool Local(PointerEventData eventData, out Vector2 local)
	{
		RectTransform parent = (RectTransform)_window.parent;
		if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, eventData.position, eventData.pressEventCamera, out local))
		{
			return false;
		}
		local -= parent.rect.min;
		return true;
	}

	private Vector2 Clamp(Vector2 position)
	{
		RectTransform parent = (RectTransform)_window.parent;
		Vector2 size = _window.rect.size;
		float x = Mathf.Clamp(position.x, 0f, Mathf.Max(0f, parent.rect.width - size.x));
		float y = Mathf.Clamp(position.y, 0f, Mathf.Max(0f, parent.rect.height - size.y));
		return new Vector2(x, y);
	}
}
