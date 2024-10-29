using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ScriptableObjectAudioSystem;

public class Test : MonoBehaviour
{
    public SoundData data;
    public float delay = 0.1f;

    IEnumerator Start()
    {
        while(true)
        {
            data.PlayRandom();

            yield return new WaitForSeconds(delay);   
        }

        yield return 0f;   
    }
}
