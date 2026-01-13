using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;

namespace MyFirstPlugin;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInProcess("ULTRAKILL.exe")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Logger;

    const int width = 320;
    const int height = 240;
    const int pixelSize = width * height * 4;

    const int maxInstances = 3;
    public static int instanceNum = -1;
    public static bool Produces { get { return instanceNum > 1; } }
    public static bool Consumes { get { return instanceNum < maxInstances && instanceNum != -1; } }

    MemoryMappedFile mmfIn;
    MemoryMappedViewAccessor accessorIn;
    MemoryMappedFile mmfOut;
    MemoryMappedViewAccessor accessorOut;

    internal static Texture2D texture;

    private void Awake()
    {
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

        var instance = new Harmony(MyPluginInfo.PLUGIN_GUID);
        instance.PatchAll(typeof(Patches));

        Application.runInBackground = true;

        // CPU-side Texture2D
        texture = new Texture2D(width, height, TextureFormat.RGBA32, false);

        //Ultrakill1 is the pipe from 2 -> 1
        //Ultrakill2 is the pipe from 3 -> 2
        //First instance tries to make 1, sees that it doesn't exist and thus becomes instnace 1 and has no "Out"
        //Second instance tries to make 1, failes, tries to make 2, succeeds and thus becomes isntance 2. Its out is then Ultrakill1
        //Second instance tries to make 1, failes, tries to make 2, failes, tries to make 3, succeeds and thus becomes isntance 3. Its out is then Ultrakill2
        //All have an in, but all may not have an out
        for (int i = 1; i <= maxInstances; i++)
        {
            try
            {
                mmfIn = MemoryMappedFile.OpenExisting($"Ultrakill{i}", MemoryMappedFileRights.Read);
                //If we get here, someone has already opened Ultrakilli so we try the next number
                mmfIn.Dispose();
                continue;
            }
            catch (FileNotFoundException)
            {
                //We have found a file which does not exist, so i must be our number!
                instanceNum = i;
                Logger.LogInfo($"Our instance num is: {instanceNum}, Producer: {Produces}, Consumer: {Consumes}");

                if (Consumes)
                {
                    mmfIn = MemoryMappedFile.CreateOrOpen($"Ultrakill{i}", pixelSize, MemoryMappedFileAccess.ReadWrite);
                    accessorIn = mmfIn.CreateViewAccessor(0, pixelSize, MemoryMappedFileAccess.ReadWrite);
                }

                if (Produces)
                {
                    mmfOut = MemoryMappedFile.OpenExisting($"Ultrakill{i - 1}", MemoryMappedFileRights.ReadWrite);
                    accessorOut = mmfOut.CreateViewAccessor(0, pixelSize, MemoryMappedFileAccess.ReadWrite);
                }

                break;
            }
        }
    }

    void Update()
    {
        if (Produces)
        {
            Texture2D screenTex = ScreenCapture.CaptureScreenshotAsTexture();

            Texture2D resized = new Texture2D(width, height, TextureFormat.RGBA32, false);
            Color[] pixels = screenTex.GetPixels();
            Color[] resizedPixels = new Color[width * height];

            //Simple nearest-neighbor rescale
            for (int y = 0; y < height; y++)
            {
                int srcY = y * screenTex.height / height;
                for (int x = 0; x < width; x++)
                {
                    int srcX = x * screenTex.width / width;
                    resizedPixels[y * width + x] = pixels[srcY * screenTex.width + srcX];
                }
            }

            resized.SetPixels(resizedPixels);
            resized.Apply();

            Graphics.CopyTexture(resized, texture);

            byte[] raw = texture.GetRawTextureData();
            accessorOut.WriteArray(0, raw, 0, raw.Length);

            UnityEngine.Object.Destroy(screenTex);
            UnityEngine.Object.Destroy(resized);
        }

        if (Consumes)
        {
            byte[] pixels = new byte[pixelSize];
            accessorIn.ReadArray(0, pixels, 0, pixelSize);

            texture.LoadRawTextureData(pixels);
            texture.Apply(false, false);
        }

        if (Input.GetKeyDown(KeyCode.I) || Input.GetKeyDown(KeyCode.O)) //Into
        {
            Process currentProces = Process.GetCurrentProcess();
            string name = currentProces.ProcessName;
            List<Process> processes = Process.GetProcessesByName(name) //Inefficient but i just wanna get this out
                                             .OrderBy(p =>
                                             {
                                                 try { return p.StartTime; }
                                                 catch { return DateTime.MaxValue; }
                                             })
                                             .ToList();
            int ourPos = processes.FindIndex(p =>
            {
                try { return p.Id == currentProces.Id; }
                catch { return false; }
            });
            Logger.LogInfo($"Num processes: {processes.Count}, our pos: {ourPos}");
            if (Input.GetKeyDown(KeyCode.I) && ourPos != processes.Count - 1)
                WindowFocus.TryActivate(processes[ourPos + 1]);
            if (Input.GetKeyDown(KeyCode.O) && ourPos != 0)
                WindowFocus.TryActivate(processes[ourPos - 1]);
        }
    }

    void OnDestroy()
    {
        mmfIn?.Dispose();
        accessorIn?.Dispose();
        mmfOut?.Dispose();
        accessorOut?.Dispose();
    }
}
public static class Patches
{
    [HarmonyPatch(typeof(ShopZone), "Start")]
    [HarmonyPrefix]
    static void Hook(ShopZone __instance)
    {
        if (!Plugin.Consumes) return;
        Transform mainPanel = __instance.transform.Find("Canvas/Background/Main Panel");
        GameObject tipOfTheDay = mainPanel.Find("Tip of the Day").gameObject;
        GameObject UI = GameObject.Instantiate(tipOfTheDay, mainPanel);
        UI.transform.SetAsLastSibling();

        UI.GetComponent<RectTransform>().offsetMin = Vector3.zero;
        UI.transform.Find("Icon").GetComponent<Image>().sprite = new UKAsset<UnityEngine.Sprite>("Assets/Textures/UI/smileOS 2 icon gun.png").Asset;
        UI.GetComponent<Image>().raycastTarget = true; //Block pressing buttons behind
        GameObject screenGO = UI.transform.Find("Panel/Text Inset").gameObject;
        GameObject.Destroy(screenGO.transform.GetChild(0).gameObject); //Destroy the text
        GameObject.DestroyImmediate(screenGO.GetComponent<Image>());

        RectTransform rt = screenGO.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = Vector2.zero;

        screenGO.AddComponent<RawImage>().texture = Plugin.texture;

        UI.SetActive(false);
        GameObject backButton = UI.transform.Find("Button 1").gameObject;
        backButton.AddComponent<Button>().onClick.AddListener(() => UI.SetActive(false));
        backButton.AddComponent<ShopButton>(); //Gives an error but i don't care
        backButton.GetComponent<Image>().raycastTarget = true;

        //Some great code here lol
        try
        {
            ShopButton cgButton = mainPanel.Find("Main Menu/Buttons/CyberGrindButton").GetComponent<ShopButton>();
            cgButton.toActivate = [];
            cgButton.toDeactivate = [];
            cgButton.gameObject.GetComponent<Button>().onClick.AddListener(() =>
            {
                UI.transform.Find("Title").GetComponent<TMPro.TMP_Text>().text = "THE CYBER GRIND";
                UI.SetActive(true);
            });
        }
        catch { }
        try
        {
            ShopButton cgButton = mainPanel.Find("Main Menu/Buttons/SandboxButton").GetComponent<ShopButton>();
            cgButton.toActivate = [];
            cgButton.toDeactivate = [];
            cgButton.gameObject.GetComponent<Button>().onClick.AddListener(() =>
            {
                UI.transform.Find("Title").GetComponent<TMPro.TMP_Text>().text = "SANDBOX";
                UI.SetActive(true);
            });
        }
        catch { }
    }
}
public static class WindowFocus
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    public const int SW_RESTORE = 9;
    public static bool TryActivate(Process process)
    {
        if (process == null)
            return false;

        if (process.MainWindowHandle == IntPtr.Zero)
            return false;

        if (IsIconic(process.MainWindowHandle))
            ShowWindow(process.MainWindowHandle, SW_RESTORE);

        return SetForegroundWindow(process.MainWindowHandle);
    }
}
