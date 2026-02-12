using UnityEngine;
using UnityEngine.InputSystem;

namespace Seb.Fluid.Demo
{
	public class OrbitCam : MonoBehaviour
	{
		public float moveSpeed = 3;
		public float rotationSpeed = 220;
		public float zoomSpeed = 0.1f;
		Vector2 mousePosOld;
		bool hasFocusOld;
		public float focusDst = 1f;
		Vector3 orbitPivot;

		void Update()
		{
			Mouse mouse = Mouse.current;
			if (mouse == null)
				return;

			if (Application.isFocused != hasFocusOld)
			{
				hasFocusOld = Application.isFocused;
				mousePosOld = mouse.position.ReadValue();
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

			// Right drag = rotate (orbit)
			if (mouse.rightButton.wasPressedThisFrame)
			{
				orbitPivot = transform.position + transform.forward * focusDst;
			}

			if (mouse.rightButton.isPressed)
			{
				transform.RotateAround(orbitPivot, transform.right, mouseMoveY * -rotationSpeed);
				transform.RotateAround(orbitPivot, Vector3.up, mouseMoveX * rotationSpeed);
			}

			transform.Translate(move);

			// Scroll = zoom only
			float mouseScroll = mouse.scroll.ReadValue().y;
			transform.Translate(Vector3.forward * mouseScroll * zoomSpeed * dstWeight);
		}

		void OnDrawGizmosSelected()
		{
			Gizmos.color = Color.red;
		}
	}
}