using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Linq;
using System.Threading.Tasks;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;
using StationeersMods.Interface;
using UnityEngine;
using Newtonsoft.Json;
using System.Globalization;

namespace IC10OCD
{
    public class ChipInfo
    {
        public string name;
        public string refid;
        public string holder_name;
        public string holder_refid;
        public string compileErrorLineNumber;
        public string compileErrorType;
        public bool on;
    }

    public class NetworkDeviceInfo
    {
        public string name;
        public string refid;
        public string uplink;
    }

    public class ChipCode
    {
        public string code;
    }

    public class ChipDump
    {
        public double[] registers;
        public double[] stack;
        public double? lineNumber;
    }

    public class ChipMemValue
    {
        public double value;
    }

    public class ChipMemSlices
    {
        public ChipMemSlice[] mem_slices;
    }
    public class ChipMemSlice
    {
        public string type; // register or stack
        public int start_addr;
        public double[] values;
    }

    [StationeersMod("IC10OCD", "IC10OCD", "0.2.5499.24517.1")]
    public class IC10OCD : ModBehaviour
    {
        // Config entry for chip data path
        private HttpListener listener;

        public override void OnLoaded(ContentHandler contentHandler)
        {
            base.OnLoaded(contentHandler);
            var ip = Config.Bind("General", "IP", @"localhost", "Address that the server will listen on").Value;
            var port = Config.Bind("General", "Port", @"8000", "Port that the server will listen on").Value;

            listener = new HttpListener();
            listener.Prefixes.Add("http://" + ip + ":" + port + "/");
            listener.Start();

            var harmony = new Harmony("IC10OCD");
            harmony.PatchAll();
            UnityEngine.Debug.Log("IC10OCD Loaded!");

            // Start coroutine to write chip data every 10 seconds
            StartCoroutine(WriteChipDataRoutine());
        }

        void OnApplicationQuit()
        {
            listener.Stop();
            Debug.Log("Server stoped");
        }

        private System.Collections.IEnumerator WriteChipDataRoutine()
        {
            while (listener.IsListening)
            {
                Task<HttpListenerContext> task = listener.GetContextAsync();
                yield return new WaitUntil(() => task.IsCompleted);

                HttpListenerContext context = task.Result;
                Debug.LogWarning("Method: " + context.Request.HttpMethod);
                Debug.LogWarning("LocalUrl: " + context.Request.Url.LocalPath);

                // https://learn.microsoft.com/en-us/dotnet/api/system.uri?view=net-9.0
                // https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistenerrequest?view=net-9.0
                // exemple for https://user:password@www.contoso.com:80/Home/Index.htm?q1=v1&q2=v2#FragmentName");
                // http: Segments: [/, Home/, Index.htm]
                string[] segments = context.Request.Url.Segments;
                Debug.LogWarning("Segments: " + String.Join(", ", segments));

                if (context.Request.HttpMethod == "POST")
                {
                    bool error = true;
                    var data_text = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding).ReadToEnd();
                    Debug.LogWarning(data_text);
                    error = ParsePostRequest(segments, data_text);
                    if (error == true)
                    {
                        context.Response.StatusCode = 404;
                    }
                    else
                    {
                        context.Response.StatusCode = 200;
                    }
                }
                else if (context.Request.HttpMethod == "GET")
                {
                    string returnString = "";
                    bool error = true;
                    (error, returnString) = ParseGetRequest(segments);
                    Debug.LogWarning("ParseGetRequest: " + error + " " + returnString);
                    if (error == true)
                    {
                        context.Response.StatusCode = 404;
                    }
                    else
                    {
                        byte[] data = Encoding.UTF8.GetBytes(returnString);
                        context.Response.ContentType = "text/html";
                        context.Response.ContentEncoding = Encoding.UTF8;
                        context.Response.ContentLength64 = data.LongLength;
                        context.Response.StatusCode = 200;
                        Task reponse = context.Response.OutputStream.WriteAsync(data, 0, data.Length);
                        yield return new WaitUntil(() => task.IsCompleted);
                    }
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
                context.Response.Close();
            }
        }

        (bool, string) ParseGetRequest(string[] pathSeg)
        {
            if (pathSeg.Count() < 2)
            {
                return (true, "");
            }
            if (pathSeg[1].Contains("list-chips"))
            {
                return (false, GetIC10List());
            }
            if (pathSeg.Count() < 3)
            {
                return (true, "");
            }
            switch (pathSeg[1])
            {
                case "chip-info/":
                    return GetIC10Info(pathSeg[2]);
                case "chip-dump/":
                    return GetIC10Dump(pathSeg[2]);
                case "chip-code/":
                    return GetIC10Code(pathSeg[2]);
                case "chip-network-device-list/":
                    return GetIC10NetworkDeviceList(pathSeg[2]);
                case "chip-mem/":
                    if (pathSeg.Count() == 5)
                        return ReadMemory(pathSeg[2], pathSeg[3], pathSeg[4]);
                    return (true, "");
                default:
                    return (true, "");
            }
        }

        bool ParsePostRequest(string[] pathSeg, string data_text)
        {
            if (pathSeg.Count() < 3)
            {
                return true;
            }
            switch (pathSeg[1])
            {
                case "chip-dump/":
                    try
                    {
                        ChipDump chipDump = JsonConvert.DeserializeObject<ChipDump>(data_text);
                        return PostIC10Dump(pathSeg[2], chipDump);
                    }
                    catch
                    {
                        return true;
                    }
                case "chip-mem-slices/":
                    try
                    {
                        ChipMemSlices chipMemSlices = JsonConvert.DeserializeObject<ChipMemSlices>(data_text);
                        return PostIC10MemSlice(pathSeg[2], chipMemSlices.mem_slices);
                    }
                    catch
                    {
                        return true;
                    }
                case "chip-code/":
                    try
                    {
                        ChipCode chipCode = JsonConvert.DeserializeObject<ChipCode>(data_text);
                        return PostIC10Code(pathSeg[2], chipCode);
                    }
                    catch
                    {
                        return true;
                    }
                case "toggle-chip-power/":
                    return ToggleChipPower(pathSeg[2]);
                case "chip-mem/":
                    if (pathSeg.Count() == 6)
                        return WriteMemory(pathSeg[2], pathSeg[3], pathSeg[4], pathSeg[5]);
                    return true;
                default:
                    return true;
            }
        }

        string GetIC10List()
        {
            var chips = FindObjectsOfType<ProgrammableChip>();
            List<ChipInfo> chipInfos = new List<ChipInfo>();
            foreach (var chip in chips)
            {
                var chipInfo = new ChipInfo();
                chipInfo.holder_name = "No Housing Found";
                chipInfo.on = false;
                if (chip.ParentSlot != null)
                {
                    var holder = chip.ParentSlot.Parent;
                    chipInfo.holder_name = holder.DisplayName;
                    chipInfo.holder_refid = holder.ReferenceId.ToString();
                    chipInfo.on = holder.OnOff;
                }
                chipInfo.refid = chip.ReferenceId.ToString() ?? "UNNAMED";
                chipInfo.name = chip.DisplayName.ToString() ?? "NO DISPLAY NAME";
                chipInfo.compileErrorLineNumber = chip.ErrorLineNumberString;
                chipInfo.compileErrorType = chip.ErrorTypeString;
                chipInfos.Add(chipInfo);
            }
            return JsonConvert.SerializeObject(chipInfos);
        }

        (bool, string) GetIC10Info(string refId)
        {
            var chips = FindObjectsOfType<ProgrammableChip>();
            var filteredChips = chips.Where(chip => chip.ReferenceId.ToString() == refId);
            Debug.LogWarning("RefId: " + refId);
            if (filteredChips.Count() == 1)
            {
                var chip = filteredChips.First();
                ChipInfo chipInfo = new ChipInfo();
                chipInfo.refid = refId;
                chipInfo.holder_name = "No Housing Found";
                chipInfo.on = false;
                if (chip.ParentSlot != null)
                {
                    var holder = chip.ParentSlot.Parent;
                    chipInfo.holder_name = holder.DisplayName;
                    chipInfo.holder_refid = holder.ReferenceId.ToString();
                    chipInfo.on = holder.OnOff;
                }
                chipInfo.name = chip.DisplayName.ToString() ?? "NO DISPLAY NAME";
                chipInfo.compileErrorLineNumber = chip.ErrorLineNumberString;
                chipInfo.compileErrorType = chip.ErrorTypeString;
                return (false, JsonConvert.SerializeObject(chipInfo));
            }
            else
            {
                return (true, "");
            }
        }

        (bool, string) GetIC10Code(string refId)
        {
            var chips = FindObjectsOfType<ProgrammableChip>();
            Debug.LogWarning("RefId: " + refId);
            var filteredChips = chips.Where(chip => chip.ReferenceId.ToString() == refId);
            if (filteredChips.Count() == 1)
            {
                var chip = filteredChips.First();
                ChipCode chipCode = new ChipCode();
                chipCode.code = chip.SourceCode;
                return (false, JsonConvert.SerializeObject(chipCode));
            }
            else
                return (true, "");
        }

        bool PostIC10Code(string refId, ChipCode chipCode)
        {
            var chips = FindObjectsOfType<ProgrammableChip>();
            Debug.LogWarning("RefId: " + refId);
            var filteredChips = chips.Where(chip => chip.ReferenceId.ToString() == refId);
            if (filteredChips.Count() == 1)
            {
                var chip = filteredChips.First();
                chip.SetSourceCode(chipCode.code);
                return false;
            }
            else
                return true;
        }

        (bool, string) GetIC10Dump(string refId)
        {
            var chips = FindObjectsOfType<ProgrammableChip>();
            Debug.LogWarning("RefId: " + refId);
            var filteredChips = chips.Where(chip => chip.ReferenceId.ToString() == refId);
            if (filteredChips.Count() == 1)
            {
                var chip = filteredChips.First();
                ChipDump chipMemory = new ChipDump();

                double[] stack = Traverse.Create(chip).Field("_Stack").GetValue() as double[];
                chipMemory.stack = stack;

                double[] registers = Traverse.Create(chip).Field("_Registers").GetValue() as double[];
                chipMemory.registers = registers;
                chipMemory.lineNumber = chip.LineNumber;
                return (false, JsonConvert.SerializeObject(chipMemory));
            }
            else
                return (true, "");
        }

        (bool, string) GetIC10NetworkDeviceList(string refId)
        {
            var chips = FindObjectsOfType<ProgrammableChip>();
            Debug.LogWarning("RefId: " + refId);
            var filteredChips = chips.Where(chip => chip.ReferenceId.ToString() == refId);
            if (filteredChips.Count() == 1)
            {
                var chip = filteredChips.First();
                if (chip.ParentSlot != null)
                {
                    var holder = chip.ParentSlot.Parent;
                    try
                    {
                        var network_list = (holder as LogicUnitBase).InputNetwork1.DeviceList;
                        List<NetworkDeviceInfo> networkDeviceInfoList = new List<NetworkDeviceInfo>();
                        foreach (var networkDevice in network_list)
                        {
                            NetworkDeviceInfo deviceInfo = new NetworkDeviceInfo
                            {
                                name = networkDevice.DisplayName,
                                refid = networkDevice.ReferenceId.ToString(),
                                uplink = "",
                            };
                            networkDeviceInfoList.Add(deviceInfo);

                            if (networkDevice is RocketDataUpLink)
                            {
                                if ((networkDevice as RocketDataUpLink).ConnectedDataNetTransmitter != null)
                                {
                                    var mirroredDownLinkNetworkDevices = (networkDevice as RocketDataUpLink).ConnectedDataNetTransmitter.DataCableNetwork.DeviceList;
                                    foreach (var mirroredDownLinkedNetworkDevice in mirroredDownLinkNetworkDevices)
                                    {
                                        NetworkDeviceInfo mirroredDownLinkedDeviceInfo = new NetworkDeviceInfo
                                        {
                                            name = mirroredDownLinkedNetworkDevice.DisplayName,
                                            refid = mirroredDownLinkedNetworkDevice.ReferenceId.ToString(),
                                            uplink = networkDevice.DisplayName,
                                        };
                                        networkDeviceInfoList.Add(mirroredDownLinkedDeviceInfo);
                                    }
                                }
                            }
                        }
                        return (false, JsonConvert.SerializeObject(networkDeviceInfoList));
                    }
                    catch
                    {
                        return (true, "");
                    }
                }
                else
                {
                    return (false, "{}");
                }
            }
            else
                return (true, "");
        }

        bool PostIC10Dump(string refId, ChipDump chipDump)
        {
            var chips = FindObjectsOfType<ProgrammableChip>();
            Debug.LogWarning("RefId: " + refId);
            var filteredChips = chips.Where(chip => chip.ReferenceId.ToString() == refId);
            if (filteredChips.Count() == 1)
            {
                var chip = filteredChips.First();
                if (chipDump.stack.Length == 512)
                {
                    Traverse.Create(chip).Field("_Stack").SetValue(chipDump.stack);
                }
                if (chipDump.stack.Length == 18)
                {
                    Traverse.Create(chip).Field("_Register").SetValue(chipDump.registers);
                }
                return false;
            }
            else
                return true;
        }

        bool PostIC10MemSlice(string refId, ChipMemSlice[] chipMemSlices)
        {
            var chips = FindObjectsOfType<ProgrammableChip>();
            Debug.LogWarning("RefId: " + refId);
            var filteredChips = chips.Where(chip => chip.ReferenceId.ToString() == refId);
            if (filteredChips.Count() == 1)
            {
                var chip = filteredChips.First();
                foreach (var chipMemSlice in chipMemSlices)
                {
                    if (chipMemSlice.type == "stack")
                    {
                        int start_addr = chipMemSlice.start_addr;
                        for (int i = 0; i < chipMemSlice.values.Length; i++)
                        {
                            if (start_addr + i >= 512)
                                break;
                            chip.WriteMemory(start_addr + i, chipMemSlice.values[i]);
                        }
                    }
                    else if (chipMemSlice.type == "register")
                    {
                        double[] registers = Traverse.Create(chip).Field("_Registers").GetValue() as double[];
                        int start_addr = chipMemSlice.start_addr;
                        for (int i = 0; i < chipMemSlice.values.Length; i++)
                        {
                            if (start_addr + i >= 18)
                                break;
                            registers[start_addr + i] = chipMemSlice.values[i];
                        }
                        Traverse.Create(chip).Field("_Register").SetValue(registers);
                    }
                }
                return false;
            }
            else
                return true;
        }

        bool PostClearStack(string refId)
        {
            var chips = FindObjectsOfType<ProgrammableChip>();
            Debug.LogWarning("RefId: " + refId);
            var filteredChips = chips.Where(chip => chip.ReferenceId.ToString() == refId);
            if (filteredChips.Count() == 1)
            {
                var chip = filteredChips.First();
                chip.ClearMemory();
                return false;
            }
            else
                return true;
        }

        bool ToggleChipPower(string refId)
        {
            try
            {
                var chips = FindObjectsOfType<ProgrammableChip>();
                Debug.LogWarning("RefId: " + refId);
                var filteredChips = chips.Where(chip => chip.ReferenceId.ToString() == refId);
                if (filteredChips.Count() == 1)
                {
                    var chip = filteredChips.First();
                    if (chip.ParentSlot != null)
                    {
                        var holder = chip.ParentSlot.Parent;
                        holder.OnOff = !holder.OnOff;
                        return false;
                    }
                    else
                    {
                        return true;
                    }
                }
                else
                    return true;
            }
            catch
            {
                return true;
            }
        }

        (bool, string) ReadMemory(string refId, string memTypeUri, string memAddressUri)
        {
            refId.Replace("/", "");
            memTypeUri.Replace("/", "");
            try
            {
                int memAddress = Int32.Parse(memAddressUri);
                var chips = FindObjectsOfType<ProgrammableChip>();
                Debug.LogWarning("RefId: " + refId);
                var filteredChips = chips.Where(chip => chip.ReferenceId.ToString() == refId);
                if (filteredChips.Count() == 1)
                {
                    var chip = filteredChips.First();
                    var memValue = new ChipMemValue();
                    if (memTypeUri == "stack")
                    {
                        memValue.value = chip.ReadMemory(memAddress);
                        return (false, memValue.ToString());
                    }
                    else if (memTypeUri == "register")
                    {
                        if (memAddress > 17 || memAddress < 0)
                        {
                            return (true, "");
                        }
                        double[] registers = Traverse.Create(chip).Field("_Registers").GetValue() as double[];
                        memValue.value = registers[memAddress];
                    }
                    else
                    {
                        return (true, "");
                    }
                    return (false, JsonConvert.SerializeObject(memValue));
                }
                else
                    return (true, "");
            }
            catch
            {
                return (true, "");
            }
        }

        bool WriteMemory(string refId, string memTypeUri, string memAddressUri, string value)
        {
            refId.Replace("/", "");
            memTypeUri.Replace("/", "");
            try
            {
                int memAddress = Int32.Parse(memAddressUri);
                double memValue = Double.Parse(value, CultureInfo.InvariantCulture);
                var chips = FindObjectsOfType<ProgrammableChip>();
                Debug.LogWarning("RefId: " + refId);
                var filteredChips = chips.Where(chip => chip.ReferenceId.ToString() == refId);
                if (filteredChips.Count() == 1)
                {
                    var chip = filteredChips.First();
                    if (memTypeUri == "stack")
                    {
                        chip.WriteMemory(memAddress, memValue);
                        return false;
                    }
                    else if (memTypeUri == "register")
                    {
                        if (memAddress > 17 || memAddress < 0)
                        {
                            return true;
                        }
                        double[] registers = Traverse.Create(chip).Field("_Registers").GetValue() as double[];
                        registers[memAddress] = memValue;
                        Traverse.Create(chip).Field("_Register").SetValue(registers);
                        return false;
                    }
                    else
                    {
                        return true;
                    }
                }
                else
                    return true;
            }
            catch
            {
                return true;
            }
        }
    }
}
