using UnityEngine;

// Keyboard equivalents of the FERL session menu and object palette for headset-free testing.
public class FERLKeyboardShortcuts : MonoBehaviour
{
    [SerializeField] private FERLSessionMenuController menu;
    [SerializeField] private FERLObjectPaletteController palette;

    [Header("Session keys")]
    [SerializeField] private KeyCode setStart = KeyCode.Alpha1;
    [SerializeField] private KeyCode setGoal = KeyCode.Alpha2;
    [SerializeField] private KeyCode ikEndpoints = KeyCode.I;
    [SerializeField] private KeyCode plan = KeyCode.P;
    [SerializeField] private KeyCode playStop = KeyCode.Space;
    [SerializeField] private KeyCode robotTrace = KeyCode.R;
    [SerializeField] private KeyCode envTrace = KeyCode.T;
    [SerializeField] private KeyCode envCorrection = KeyCode.C;
    [SerializeField] private KeyCode learn = KeyCode.L;
    [SerializeField] private KeyCode learnOnly = KeyCode.K;
    [SerializeField] private KeyCode replan = KeyCode.N;
    [SerializeField] private KeyCode save = KeyCode.F5;
    [SerializeField] private KeyCode reset = KeyCode.F9;
    [SerializeField] private KeyCode sync = KeyCode.F1;
    [SerializeField] private KeyCode rewardMap = KeyCode.M;

    [Header("Palette keys (with Shift)")]
    [SerializeField] private KeyCode[] spawnKeys =
    {
        KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6
    };

    private void Update()
    {
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        if (shift && palette != null)
        {
            for (int i = 0; i < spawnKeys.Length && i < FerlObjectTypes.Names.Length; i++)
            {
                if (Input.GetKeyDown(spawnKeys[i]))
                {
                    palette.Spawn((FerlObjectType)i);
                    return;
                }
            }
        }

        if (menu == null || shift)
            return;
        if (Input.GetKeyDown(setStart)) menu.SetStart();
        else if (Input.GetKeyDown(setGoal)) menu.SetGoal();
        else if (Input.GetKeyDown(ikEndpoints)) menu.IkEndpoints();
        else if (Input.GetKeyDown(plan)) menu.Plan();
        else if (Input.GetKeyDown(playStop)) menu.TogglePlay();
        else if (Input.GetKeyDown(robotTrace)) menu.ToggleRobotTrace();
        else if (Input.GetKeyDown(envTrace)) menu.ToggleEnvTrace();
        else if (Input.GetKeyDown(envCorrection)) menu.EnvCorrection();
        else if (Input.GetKeyDown(learn)) menu.Learn();
        else if (Input.GetKeyDown(learnOnly)) menu.LearnOnly();
        else if (Input.GetKeyDown(replan)) menu.Replan();
        else if (Input.GetKeyDown(save)) menu.Save();
        else if (Input.GetKeyDown(reset)) menu.ResetSession();
        else if (Input.GetKeyDown(sync)) menu.Sync();
        else if (Input.GetKeyDown(rewardMap)) menu.CycleRewardMap();
    }
}
