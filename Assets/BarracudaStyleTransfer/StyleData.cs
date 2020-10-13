using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class StyleDataLayer
{
    public float[] data;
}

public class StyleData : ScriptableObject
{
    public List<StyleDataLayer> layerData = new List<StyleDataLayer>();
}
