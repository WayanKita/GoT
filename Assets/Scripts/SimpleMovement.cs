using UnityEngine;

public class SimpleMovement : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 5f;
    public float verticalSpeed = 3f;

    [Header("Rotation")]
    public float rotationSpeed = 90f; // degrees per second

    void Update()
    {
        float dt = Time.deltaTime;

        // WASD movement in local space
        Vector3 move = Vector3.zero;

        if (Input.GetKey(KeyCode.W))
            move += transform.forward;

        if (Input.GetKey(KeyCode.S))
            move -= transform.forward;

        if (Input.GetKey(KeyCode.D))
            move += transform.right;

        if (Input.GetKey(KeyCode.A))
            move -= transform.right;

        transform.position += move.normalized * moveSpeed * dt;

        // Vertical movement
        if (Input.GetKey(KeyCode.UpArrow))
            transform.position += Vector3.up * verticalSpeed * dt;

        if (Input.GetKey(KeyCode.DownArrow))
            transform.position += Vector3.down * verticalSpeed * dt;

        // Rotation around Y-axis
        float rotation = 0f;

        if (Input.GetKey(KeyCode.LeftArrow))
            rotation -= rotationSpeed * dt;

        if (Input.GetKey(KeyCode.RightArrow))
            rotation += rotationSpeed * dt;

        transform.Rotate(0f, rotation, 0f, Space.World);
    }
}