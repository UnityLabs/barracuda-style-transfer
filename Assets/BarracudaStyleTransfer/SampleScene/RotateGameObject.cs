using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RotateGameObject : MonoBehaviour
{
    public float rotateSpeed;
    public Vector3 rotateAxis;

    // Start is called before the first frame update
    void Start()
    {

    }

    // Update is called once per frame
    void FixedUpdate()
    {
        transform.Rotate(rotateAxis.normalized, rotateSpeed);
    }
}
