using UnityEngine;
using UnityEngine.InputSystem;

namespace Seb.Fluid.Demo
{
	public class OrbitCam : MonoBehaviour
	{
		public float moveSpeed = 3;
		public float rotationSpeed = 220;
		public float zoomSpeed = 0.1f;
		public Vector3 pivot;
		Vector2 mousePosOld;
		bool hasFocusOld;
		public float focusDst = 1f;
		Vector3 lastCtrlPivot;

		float lastLeftClickTime = float.MinValue;
		private Vector2 rightClickPos;

		private Vector3 startPos;
		Quaternion startRot;

		void Start()
		{
			startPos = transform.position;
			startRot = transform.rotation;
		}

		void Update()
		{
			Mouse mouse = Mouse.current;
			Keyboard keyboard = Keyboard.current;

			if (mouse == null || keyboard == null)
				return;

			if (Application.isFocused != hasFocusOld)
			{
				hasFocusOld = Application.isFocused;
				mousePosOld = mouse.position.ReadValue();
			}

			// Reset view on double click
			if (mouse.leftButton.wasPressedThisFrame)
			{
				if (Time.time - lastLeftClickTime < 0.2f)
				{
					transform.position = startPos;
					transform.rotation = startRot;
				}

				lastLeftClickTime = Time.time;
			}

			float dstWeight = transform.position.magnitude;
			Vector2 mousePos = mouse.position.ReadValue();
			Vector2 mouseMove = mousePos - mousePosOld;
			mousePosOld = mousePos;
			float mouseMoveX = mouseMove.x / Screen.width;
			float mouseMoveY = mouseMove.y / Screen.width;
			Vector3 move = Vector3.zero;

			if (mouse.middleButton.isPressed)
			{
				move += Vector3.up * mouseMoveY * -moveSpeed * dstWeight;
				move += Vector3.right * mouseMoveX * -moveSpeed * dstWeight;
			}

			if (mouse.leftButton.wasPressedThisFrame)
			{
				lastCtrlPivot = transform.position + transform.forward * focusDst;
			}

			if (mouse.leftButton.isPressed)
			{
				Vector3 activePivot = keyboard.leftAltKey.isPressed ? transform.position : pivot;
				if (keyboard.leftCtrlKey.isPressed)
				{
					activePivot = lastCtrlPivot;
				}

				transform.RotateAround(activePivot, transform.right, mouseMoveY * -rotationSpeed);
				transform.RotateAround(activePivot, Vector3.up, mouseMoveX * rotationSpeed);
			}

			transform.Translate(move);

			//Scroll to zoom
			float mouseScroll = mouse.scroll.ReadValue().y;
			if (mouse.rightButton.wasPressedThisFrame)
			{
				rightClickPos = mouse.position.ReadValue();
			}

			if (mouse.rightButton.isPressed)
			{
				Vector2 delta = mouse.position.ReadValue() - rightClickPos;
				rightClickPos = mouse.position.ReadValue();
				mouseScroll = delta.magnitude * Mathf.Sign(Mathf.Abs(delta.x) > Mathf.Abs(delta.y) ? delta.x : -delta.y) / Screen.width * zoomSpeed * 100;
			}

			transform.Translate(Vector3.forward * mouseScroll * zoomSpeed * dstWeight);
		}

		void OnDrawGizmosSelected()
		{
			Gizmos.color = Color.red;
			// Gizmos.DrawWireSphere(pivot, 0.15f);
		}
	}
}