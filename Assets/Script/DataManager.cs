using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class DataManager : MonoBehaviour
{
    DataProcessor connector;
    public bool showDistance;
    public Text text;
    // Start is called before the first frame update
    void Start()
    {
        connector = FindFirstObjectByType<DataProcessor>();
        #if UNITY_EDITOR
        Debug.Log(connector ? "FindConnector" : "ConnectorDontExist");
        #endif
    }

    float currentDistance;
    // Update is called once per frame
    void Update()
    {
        if (showDistance && connector)
        {
            if(connector.distance < 4500)// 数据失真时不更新
            {
                text.text = Mathf.Lerp(currentDistance,connector.distance/1000,Time.deltaTime).ToString();
                currentDistance = connector.distance/1000;
            }

        }
            
    }
}
