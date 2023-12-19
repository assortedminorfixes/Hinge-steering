using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    public class Program : MyGridProgram
    {
        #region in-game

        //    Blargmode's Hinge steering v1.4.0 (2020-11-09)
        //    Updated v1.4.1 (2023-12-15)


        //    == Description ==
        //    This script let you create wheel loader like vehicles that turn by
        //    bending on the middle.


        //    == How to install ==
        //    Add to programmable block.
        //    Check if it managed to set itself up. If not, it will tell you what's
        //    wrong in the script output.


        //    == Turning the wrong way? ==
        //    Use the tag on the hinge with a minus sign in front, like this:
        //
        //        -#steer
        //
        //    Then recompile. That inverts the steering on that hinge.


        //    == How many hinges? ==
        //    There's no limit in the code. It's probably wise to only have one 
        //    controlled hinge per joint. If you have more than one, set the braking
        //    force to 0 on the others.


        //    == Does it work with rotors? ==
        //    Yes, but auto straighten won't work unless you set the upper limit.
        //    It's easier if you put the rotor with 0° pointing forward but you can set
        //    a center offset by adding e.g. "c90" to the rotor's name. c is for center,
        //    90 is for 90°. You can also use negative values. 
        //    Center offset works on hinges too. Recompile the script after changing this. 


        //    == Toggle Auto straighten ==
        //    It can be changed in the settings below, but it can also be toggled while
        //    driving by running the programmable block with this command:
        //
        //        straighten [on|off|onoff]
        //
        //    Where the thigns in brackets are optional options. No option is the same as 
        //    onoff, it toggles it. On and Off forces that option.
        //    So examples:
        //
        //        Toggle: straighten
        //        But also: straighten onoff
        //        Set to on: straighten on


        //    == Change Axis ==
        //    By default it uses the turning keys [A] and [D]. You can change this 
        //    individually per hinge. Just tag it and add one of the following modifiers:
        //
        //        turn    (The default value, not needed, using [A] and [D])
        //        forward    (Using [W] and [S])
        //        up    (Using [space] and [C])
        //        roll    (Using [Q] and [E])
        //        yaw    (Using [<] and [>] (and mouse))
        //        pitch    (Using [^] and [v] (and mouse))
        //
        //    It would look like this in a name: "Hinge #steer roll"
        //    Recompile the script after changing this.




        //    == Settings == 
        //----------------------------------------------

        //    Steering Speed
        //    Default 10.
        public static readonly float defaultSteeringSpeed = 1;

        //    Automatically straighten
        //    Whether steering should return to straight when releasing the turning buttons.
        //    Default true.
        public static bool autoStraighten = true;

        //    Tag
        //    Used in the name of a block to identify hinges and a control seat that
        //    should be controlled. Not needed if you have only one hinge and sit in the
        //    desired seat when starting the script.
        //    Default #steer
        public static readonly string tag = "#steer";
        public static readonly string auto = "auto";

        //    Message Expiration Time in seconds
        public static readonly float messageTime = 60;


        // Configuration Section
        private static readonly string configSection = "SteeringConfig";

        // Try to stop objects before the hit the limit.
        private static readonly float defaultSafeLimits = 0.9f;
        private static readonly bool defaultUseSafeLimits = true;

        bool UpdateBlockCustomData = true;

        //----------------------------------------------

        // End of desciption and settings. Don't touch anything below unless you want to ;)

        // Note on the version structure:
        // v1.2.3
        // v<backwards compatabillity breaking change> . <backwards compatible feature addition> . <bugfix>

        const int MinTextWidth = 20;

        static float pistonVelocityScaling = 10f;

        private static readonly MyIni _ini = new MyIni();

        public static readonly TimeSpan messageExpiration = TimeSpan.FromSeconds(messageTime);

        List<HingeOrPiston> MovableBlocks;
        Cockpit cockpit;

        State state = State.Setup;

        const int turnCeil = 30; // How many ticks a button is held to reach max turnig speed
        string errorMsg = "";
        int setupRetryIn = 0;

        HashSet<ExpiringMessage> expiringMessages;

        Dictionary<ControlAxis, InputCount> inputAxisCounter; // Counting how long a button has been held

        enum State
        {
            Setup = 0,
            On = 2,
            Off = 4,
            WindDown = 16 // Used after exiting the cockpit while steering. Should be turned off once zero point is reached.
        }

        // These axis are from a cars perspective.
        // Should probably be renamed
        public enum ControlAxis
        {
            Turn,
            Forward,
            Roll,
            Up,
            Yaw,
            Pitch,
            None
        }

        public class HingeOrPiston
        {

            public IMyMechanicalConnectionBlock block;
            public bool autoStraightenMe = autoStraighten;
            public int direction; // 1 or -1 to invert a hinge.
            public ControlAxis axis;
            public float center; // Offset from 0°. Mostly for if you place a rotor in the wrong rotation.
            public List<string> messages;
            float steeringSpeed = defaultSteeringSpeed;
            bool UseSafeLimits = defaultUseSafeLimits;
            float SafeLimits = defaultSafeLimits;

            public HingeOrPiston(IMyMechanicalConnectionBlock item)
            {
                block = item;

                messages = new List<string>();
                if (item.CustomData == "" || !ConfigureFromData(item.CustomData))
                {
                    ConfigureFromName(item.CustomName);
                }

                UpdateIni();
            }

            private bool ConfigureFromData(string customData)
            {
                _ini.Clear();

                float LowLimit = 0, HighLimit = 1;

                MyIniParseResult result;

                if (!_ini.TryParse(customData, out result) || !_ini.ContainsSection(configSection))
                {
                    messages.Add($"{result}");
                    return false;
                }

                direction = _ini.Get(configSection, "Direction").ToInt16(1);

                axis = CheckControlAxis(tag + " " + _ini.Get(configSection, "axis").ToString());

                float offset = _ini.Get(configSection, "CenterOffset").ToSingle(0);

                if (block is IMyMotorStator)
                {
                    IMyMotorStator stator = (IMyMotorStator)block;
                    center = MathHelper.ToRadians(offset);
                    LowLimit = MathHelper.ToDegrees(stator.LowerLimitRad);
                    HighLimit = MathHelper.ToDegrees(stator.UpperLimitRad);

                }
                else
                {
                    center = offset;
                }

                autoStraightenMe = _ini.Get(configSection, "AutoStraighten").ToBoolean(autoStraighten);

                SafeLimits = _ini.Get(configSection, "SafeLimits").ToSingle(defaultSafeLimits);
                UseSafeLimits = _ini.Get(configSection, "UseSafeLimits").ToBoolean(defaultUseSafeLimits);

                steeringSpeed = _ini.Get(configSection, "SteeringSpeed").ToSingle(defaultSteeringSpeed);

                messages.Add($"Parsed hinge/piston '{block.CustomName}' with Auto straighten: {(autoStraightenMe ? "On" : "Off")}, Center: {offset}, Limits: [{LowLimit}, {HighLimit}]");

                return true;
            }

            void UpdateIni()
            {
                _ini.Clear();

                _ini.Set(configSection, "Direction", direction);
                _ini.Set(configSection, "AutoStraighten", autoStraightenMe);
                _ini.Set(configSection, "UseSafeLimits", UseSafeLimits);
                _ini.Set(configSection, "SafeLimits", SafeLimits);
                _ini.Set(configSection, "SteeringSpeed", steeringSpeed);



                if (block is IMyMotorStator)
                {
                    _ini.Set(configSection, "CenterOffset", MathHelper.ToDegrees(center));
                }
                else
                {
                    _ini.Set(configSection, "CenterOffset", center);
                }

                _ini.Set(configSection, "axis", axis.ToString());
            }

            public void Save()
            {
                UpdateIni();
                block.CustomData = _ini.ToString();
            }

            void ConfigureFromName(string name)
            {
                string lname = name.ToLower();
                direction = (lname.Contains("-" + tag) ? -1 : 1);

                axis = CheckControlAxis(lname);
                center = CheckOffset(lname);

                if (lname.Contains(tag) && lname.Contains(auto))
                {
                    autoStraightenMe = !lname.Contains("-" + auto);
                    messages.Add($"Parsed hinge/piston '{name}' with Auto straighten: {(autoStraightenMe ? "On" : "Off")}");
                }
                else
                {
                    messages.Add($"Parsed hinge/piston '{name}' with default auto straighten: {(autoStraighten ? "On" : "Off")}");
                }
            }

            ControlAxis CheckControlAxis(string name)
            {
                name = name.ToLower();
                if (name.Contains(tag + " forward"))
                {
                    messages.Add($"Buttons parsed as 'forward' from '{name}'");
                    return ControlAxis.Forward;
                }
                else if (name.Contains(tag + " roll"))
                {
                    messages.Add($"Buttons parsed as 'roll' from '{name}'");
                    return ControlAxis.Roll;
                }
                else if (name.Contains(tag + " up"))
                {
                    messages.Add($"Buttons parsed as 'up' from '{name}'");
                    return ControlAxis.Up;
                }
                else if (name.Contains(tag + " yaw"))
                {
                    messages.Add($"Buttons parsed as 'yaw' from '{name}'");
                    return ControlAxis.Yaw;
                }
                else if (name.Contains(tag + " pitch"))
                {
                    messages.Add($"Buttons parsed as 'pitch' from '{name}'");
                    return ControlAxis.Pitch;
                }
                else if (name.Contains(tag + " none"))
                {
                    messages.Add($"Buttons parsed as 'none' from '{name}'");
                    return ControlAxis.None;
                }
                else
                {
                    messages.Add($"No buttons parsed from '{name}', using 'turn'");
                    return ControlAxis.Turn;
                }
            }

            // Checks if offset is defined and retuns the value converted to radians, or zero.
            float CheckOffset(string name)
            {
                var reg = new System.Text.RegularExpressions.Regex(@"(c-*[0-9])\d*");

                var match = reg.Match(name);

                if (match.Success)
                {
                    float offset = float.Parse(match.Value.Substring(1));

                    messages.Add($"{offset.ToString()}° center offset parsed from '{name}'.");

                    return MathHelper.ToRadians(offset);
                }

                messages.Add($"Parsed no center offset angle from '{name}', using 0°.");
                return 0f;
            }

            float NormalizedPosition()
            {
                if (block is IMyMotorStator)
                {
                    // Rotor limits
                    return NormalizedPositionForV(Position);
                }
                else
                {
                    // Piston limit
                    return ((IMyPistonBase)block).NormalizedPosition;
                }
            }

            float NormalizedPositionForV(float Position)
            {
                float LowLimit, HighLimit;
                if (block is IMyMotorStator)
                {
                    IMyMotorStator stator = (IMyMotorStator)block;
                    // Rotor limits
                    LowLimit = Math.Max(stator.LowerLimitRad, -(float)Math.PI*2);
                    HighLimit = Math.Min(stator.UpperLimitRad, (float)Math.PI*2);

                }
                else
                {
                    IMyPistonBase piston = (IMyPistonBase)block;
                    LowLimit = piston.MinLimit;
                    HighLimit = piston.MaxLimit;
                }
                return (Position - LowLimit) / (HighLimit - LowLimit);
            }

            public bool UpdateVelocity(Cockpit cockpit_in, Dictionary<ControlAxis, InputCount> inputAxisCounter_in)
            {
                SetVelocity(CalcVelocity(cockpit_in, inputAxisCounter_in));
                return Velocity != 0f;
            }


            float ScaledDeviationFromCenter()
            {
                if (block is IMyMotorStator)
                {
                    // Rotors, use angle as a percent of scale
                    return NormalizedPositionForV(center) - Position;

                }
                else
                {
                    return center - Position;
                }
            }

            float CalcVelocity(Cockpit cockpit, Dictionary<ControlAxis, InputCount> inputAxisCounter)
            {
                float input = GetInputClamped(axis, cockpit);

                float limitedSteeringSpeed = steeringSpeed;


                if (UseSafeLimits)
                {
                    float pctOfLimit = NormalizedPosition();

                    if (input * direction > 0)
                    {
                        pctOfLimit = 1 - pctOfLimit;
                    }

                    // Full speed from 0-SafeLimits% of the position.  Once within SafeLimit%, scale linearly with percent.
                    // Speed is unlimited in direction opposite of limit.
                    limitedSteeringSpeed *= Clamp((1 - pctOfLimit)/(1 - SafeLimits), 0.1f, 1f);
                }

                if (input == 0 && autoStraightenMe)
                {
                    float turnScalingUp = (1 - ((float)(inputAxisCounter[axis].lefts + inputAxisCounter[axis].rights) / turnCeil));
                    return -steeringSpeed * turnScalingUp * Clamp(10 * ScaledDeviationFromCenter(), 0f, 1f);
                }


                if (input > 0)
                {
                    return direction * -limitedSteeringSpeed * ((float)inputAxisCounter[axis].lefts / turnCeil) * Clamp(input, 0, 1);
                }
                else if (input < 0)
                {
                    return direction * -limitedSteeringSpeed * ((float)inputAxisCounter[axis].rights / turnCeil) * Clamp(input, -1, 1);
                }
                return 0;
            }

            public float Position
            {
                get
                {
                    if (block is IMyPistonBase)
                        return ((IMyPistonBase)block).CurrentPosition;
                    else
                    {
                        return AngleToNormedAngle(((IMyMotorStator)block).Angle);
                    }
                }
            }

            static float AngleToNormedAngle(float angle)
            {
                return (float)(((double)angle + Math.PI) % (2*Math.PI) - Math.PI);
            }

            float Velocity
            {
                get
                {
                    if (block is IMyPistonBase)
                        return ((IMyPistonBase)block).Velocity;
                    else
                        return ((IMyMotorStator)block).TargetVelocityRad;
                }
            }

            public void SetVelocity(float Velocity)
            {
                if (block is IMyPistonBase)
                    ((IMyPistonBase)block).Velocity = Velocity * pistonVelocityScaling;
                else
                    ((IMyMotorStator)block).TargetVelocityRad = Velocity;
            }

            public string AxisLog(int characterlimit)
            {
                int barlen = characterlimit - 12; // At least 2 characters for bar, plus rest of text
                float np = Clamp(NormalizedPosition(), 0, 1);
                int barfilllen = (int)Math.Floor(np * barlen);
                string bar = "[" + new String('#', barfilllen) + new String('-', barlen - barfilllen) + "]";
                string message = $"{bar} {NormalizedPosition() * 100,-3:F0}% {Velocity:F2}";
                return message;
                return message.Substring(0, characterlimit);
            }
        }

        public class Cockpit
        {
            IMyCockpit cockpit;
            DisplayItem screen;
            public int ScreenCharacterCount { 
                get
                {
                    if (screen != null)
                    {
                        return screen.CharacterWidth;
                    }
                    return 0;
                }
             }

            public bool IsUnderControl => cockpit.IsUnderControl;
            public Vector3 MoveIndicator => cockpit.MoveIndicator;
            public float RollIndicator => cockpit.RollIndicator;
            public Vector2 RotationIndicator => cockpit.RotationIndicator;
            public string CustomName => cockpit.CustomName;

            public Cockpit(IMyCockpit cockpit, IMyGridTerminalSystem GridTerminalSystem, int MovablesCount = 1)
            {
                this.cockpit = cockpit;
                screen = new DisplayItem(cockpit, GridTerminalSystem, MovablesCount);
            }

            public void Save()
            {
                _ini.Clear();
                // Load any existing INI data so we don't clobber it.
                _ini.TryParse(cockpit.CustomData);

                if (!_ini.ContainsSection(configSection))
                {
                    _ini.AddSection(configSection);
                }
                _ini.Set(configSection, "ActiveCockpit", true);
                if (screen != null)
                {
                    _ini.Set(configSection, "ActiveScreen", screen.ScreenSelector);
                }
                else
                {
                    _ini.Delete(configSection, "ActiveScreen");
                }

                cockpit.CustomData = _ini.ToString();
            }

            public void WriteText(string message, bool append = false)
            {
                if (screen != null)
                {
                    screen.WriteText(message, append);
                }
            }

            internal void ScaleToLines(int count)
            {
                if (screen != null)
                {
                    screen.ScaleToLines(count);
                }
            }
        }

        public class DisplayItem
        {
            IMyTextSurface screen;
            public int CharacterWidth { get; private set; }
            public string ActiveScreen { get; internal set; }
            public string ScreenSelector { get; internal set; }

            public DisplayItem(IMyTextSurface textSurface)
            {
                screen = textSurface;
                SetupScreen();
            }

            public DisplayItem(IMyCockpit cockpit, IMyGridTerminalSystem GridTerminalSystem, int MovablesCount = 1)
            {
                string LCDDisplayName;
                IMyTextSurfaceProvider surfaceProvider = (IMyTextSurfaceProvider)cockpit;
                IMyTextSurface surface_i;
                string search_screen = "";
                int ActiveScreen = -1;

                // Identify Screen for Display and scale it to fit number of axes.
                if (_ini.TryParse(cockpit.CustomData))
                {
                    // Have settings already, load them and check if we have everything.
                    ActiveScreen = _ini.Get(configSection, "ActiveScreen").ToInt32(ActiveScreen);

                    if (ActiveScreen == -1)
                    {
                        // Check if the active screen was specified using a name instead.
                        search_screen = _ini.Get(configSection, "ActiveScreen").ToString("");
                        if (search_screen.Contains(":"))
                        {
                            ScreenSelector = search_screen;
                            // Specified a blockname:lcdnunmber, retry search with that block.
                            // First look for the  block specified in the ship
                            IMyTerminalBlock newscreen = GridTerminalSystem.GetBlockWithName(search_screen.Split(':')[0]);
                            if (newscreen != null && newscreen is IMyTextSurface)
                            {
                                screen = (IMyTextSurface)newscreen;
                            }
                            else if (newscreen != null && newscreen is IMyTextSurfaceProvider)
                            {
                                if (!Int32.TryParse(search_screen.Split(':')[1], out ActiveScreen))
                                {
                                    ActiveScreen = -1;
                                }
                                surfaceProvider = (IMyTextSurfaceProvider)newscreen;
                            }
                        }
                    }
                    else
                    {
                        ScreenSelector = ActiveScreen.ToString();
                    }
                }

                if (ActiveScreen == -1 && screen == null)
                {
                    float largest_area = 0;
                    // Work on guessing which screen is the best choice.  Start with Main screens, then use the largest screen.
                    for (int i = 0; i < surfaceProvider.SurfaceCount; i++)
                    {
                        surface_i = surfaceProvider.GetSurface(i);
                        LCDDisplayName = surface_i.DisplayName.ToLower();
                        if (LCDDisplayName.Contains("main") || LCDDisplayName.Contains("large")
                            // If we were passed a screen to search for in search_screen.
                            || LCDDisplayName.Contains(search_screen.ToLower()))
                        {
                            ActiveScreen = i;
                            screen = surface_i;
                            break;
                        }
                        else if (surface_i.SurfaceSize.LengthSquared() > largest_area)
                        {
                            largest_area = surface_i.SurfaceSize.LengthSquared();
                            screen = surface_i;
                        }
                    }
                }
                else if (screen == null)
                    screen = surfaceProvider.GetSurface(ActiveScreen);

                if (screen != null && ScreenSelector == "")
                {
                    ScreenSelector = ActiveScreen.ToString();
                }

                SetupScreen(MovablesCount);
            }

            void SetupScreen(int MovablesCount = 1)
            {
                screen.ContentType = ContentType.TEXT_AND_IMAGE;
                screen.Font = "Monospace";
                screen.FontSize = 1;
                ScaleToLines(MovablesCount);
            }

            public void ScaleToLines(int lines)
            {
                StringBuilder sb = new StringBuilder();

                for (int i = 0; i < lines; i++) { sb.AppendLine("X"); }

                Vector2 fontsize = screen.MeasureStringInPixels(sb, screen.Font, 1f);

                float Scale = screen.SurfaceSize.Y / fontsize.Y;
                CharacterWidth = (int)(screen.SurfaceSize.X / (fontsize.X * Scale));

                if(CharacterWidth < MinTextWidth)
                {
                    Scale *= ((float)CharacterWidth / (float)MinTextWidth);
                    CharacterWidth = MinTextWidth;
                }

                screen.FontSize = Scale;
            }

            public void WriteText(string message, bool append = false)
            {
                if (screen != null)
                {
                    screen.WriteText(message, append);
                }
            }
        }

        struct ExpiringMessage
        {
            public DateTime expiry;
            public string message;
        }

        public class InputCount
        {
            public int lefts; // Couting how long the left button has been held in ticks.
            public int rights; // Same but right button.
        }

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update100;

            expiringMessages = new HashSet<ExpiringMessage>();

            inputAxisCounter = new Dictionary<ControlAxis, InputCount>();
        }

        public void Main(string argument, UpdateType updateType)
        {

            if (argument != "")
            {
                var commands = argument.Split(',');
                bool showAvailableCommands = false;

                foreach (var command in commands)
                {
                    switch (command.ToLower())
                    {
                        case "straighten on":
                            autoStraighten = true;
                            break;
                        case "straighten off":
                            autoStraighten = false;
                            break;
                        case "straighten toggle":
                        case "straighten onoff":
                        case "straighten":
                            autoStraighten = !autoStraighten;
                            break;
                        default:
                            showAvailableCommands = true;
                            NewExpiringMessage(TimeSpan.FromSeconds(7), $"Error: Couldn't parse command: {command}\n");
                            break;
                    }
                }

                if (showAvailableCommands)
                {
                    NewExpiringMessage(TimeSpan.FromSeconds(7), "Available commands:\n> straighten [on|off|onoff]\n");
                }
            }

            if ((updateType & UpdateType.Update100) != 0)
            {
                if (state == State.Setup)
                {
                    if (setupRetryIn > 0)
                    {
                        setupRetryIn--;
                    }
                    else
                    {
                        setupRetryIn = 5; // Retry every 5 100-tick periods.
                        if (Setup())
                        {
                            state = State.On;
                            cockpit.ScaleToLines(MovableBlocks.Count);
                        }
                    }
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (HingeOrPiston h in MovableBlocks)
                    {
                        sb.AppendLine(h.AxisLog(cockpit.ScreenCharacterCount));
                    }
                    UpdateDisplay(sb.ToString(), false);
                }

                LogOut(Info());
            }

            if ((updateType & UpdateType.Update1) != 0)
            {
                Update1();
            }
        }

        void Update1()
        {
            // Handle state
            switch (state)
            {
                case State.Setup:
                    return;

                case State.On:
                    if (!cockpit.IsUnderControl)
                    {
                        state = State.WindDown;
                    }
                    break;

                case State.Off:
                    if (cockpit.IsUnderControl)
                    {
                        state = State.On;
                    }
                    else
                    {
                        return;
                    }
                    break;

                case State.WindDown:
                    if (cockpit.IsUnderControl)
                    {
                        state = State.On;
                    }
                    break;
            }

            foreach (var axis in inputAxisCounter)
            {
                float input = GetInputClamped(axis.Key, cockpit);

                if (input > 0)
                {
                    if (axis.Value.lefts <= turnCeil) axis.Value.lefts++;
                    axis.Value.rights = 0;
                }
                else if (input < 0)
                {
                    if (axis.Value.rights <= turnCeil) axis.Value.rights++;
                    axis.Value.lefts = 0;
                }
                else // if input == 0
                {
                    if (axis.Value.lefts > 0) axis.Value.lefts--;
                    if (axis.Value.rights > 0) axis.Value.rights--;
                }
            }

            bool moving = false;
            foreach (var hinge in MovableBlocks)
            {
                moving |= hinge.UpdateVelocity(cockpit, inputAxisCounter);
            }
            if (state == State.WindDown && !moving) state = State.Off;
        }

        static float GetInputClamped(ControlAxis axis, Cockpit cockpit)
        {
            return MathHelper.Clamp(GetInput(axis, cockpit), -1, 1);
        }

        static float GetInput(ControlAxis axis, Cockpit cockpit)
        {
            // If you leave the cockpit while pressing a button, 
            // that value stays on. This prevents that.
            if (!cockpit.IsUnderControl) return 0;

            switch (axis)
            {
                case ControlAxis.Forward:
                    return cockpit.MoveIndicator.Z;
                case ControlAxis.Roll:
                    return cockpit.RollIndicator;
                case ControlAxis.Up:
                    return cockpit.MoveIndicator.Y;
                case ControlAxis.Yaw:
                    return cockpit.RotationIndicator.Y;
                case ControlAxis.Pitch:
                    return cockpit.RotationIndicator.X;
                case ControlAxis.None:
                    return 0f;
            }
            return cockpit.MoveIndicator.X;
        }

        bool Setup()
        {
            var rotors = new List<IMyMechanicalConnectionBlock>();
            GridTerminalSystem.GetBlocksOfType(rotors, x => x.IsSameConstructAs(Me) && (x is IMyMotorStator || x is IMyPistonBase));

            MovableBlocks = new List<HingeOrPiston>();

            if (rotors.Count >= 1)
            {
                // Select one with a tag.
                foreach (var item in rotors)
                {
                    if (item.CustomName.ToLower().Contains(tag) ||
                        (_ini.TryParse(item.CustomData) && _ini.ContainsSection(configSection)) ||
                        rotors.Count == 1)
                    {
                        var h = new HingeOrPiston(item);

                        foreach (string message in h.messages)
                        {
                            NewExpiringMessage(messageExpiration, message);
                        }

                        MovableBlocks.Add(h);

                        if (!inputAxisCounter.ContainsKey(h.axis))
                        {
                            inputAxisCounter.Add(h.axis, new InputCount());
                        }
                    }
                }
            }

            if (MovableBlocks.Count == 0)
            {
                errorMsg = "Could not identify hinges. Please tag the hinges you want to use by adding " + tag + " to the name of them.";
                return false;
            }

            List<IMyCockpit> cockpits = new List<IMyCockpit>();
            GridTerminalSystem.GetBlocksOfType(cockpits, x => x.IsSameConstructAs(Me));

            foreach (var item in cockpits)
            {
                if (item.IsUnderControl || item.CustomName.ToLower().Contains(tag) ||
                    // Has defined section in CustomData for our code and is set to an ActiveCockpit.
                    (_ini.TryParse(item.CustomData) && _ini.Get(configSection, "ActiveCockpit").ToBoolean())
                    || cockpits.Count == 1)
                {
                    cockpit = new Cockpit(item, GridTerminalSystem, MovableBlocks.Count);
                    break;
                }
            }

            if (cockpit == null)
            {
                errorMsg = $"Could not select cockpit. Sit in one for setup to find or tag it with {tag}.";
                return false;
            }

            if (UpdateBlockCustomData)
            {
                foreach (HingeOrPiston h in MovableBlocks)
                {
                    h.Save();
                }

                cockpit.Save();
            }

            return true;
        }


        string Info()
        {
            string text = "== Blarg's Hinge Steering ==\n\n";

            if (state != State.Setup)
            {
                text += $"Setup complete. Script {(cockpit.IsUnderControl ? "active" : "idle")}.\nHinges: {MovableBlocks.Count}.\nSelected control seat: {cockpit.CustomName}\nAuto straighten: {(autoStraighten ? "On" : "Off")}\n\n";
            }
            else
            {
                if (setupRetryIn > 0)
                {
                    text += $"Setup failed.\n\n{errorMsg}\n\nRetrying in {setupRetryIn} long seconds.\n\n";
                }
                else
                {
                    text += "Running setup...\n\n";
                }
            }

            expiringMessages.RemoveWhere(item => DateTime.Now > item.expiry);

            if (expiringMessages.Count > 0)
            {
                text += "____________________\n";

                foreach (var item in expiringMessages)
                {
                    text += item.message + "\n\n";
                }
            }

            return text;
        }

        public static float Clamp(float value, float min, float max)
        {
            return (value < min) ? min : (value > max) ? max : value;
        }

        bool NewExpiringMessage(TimeSpan lifeTime, string message)
        {
            var m = new ExpiringMessage();
            m.expiry = DateTime.Now.Add(lifeTime);
            m.message = message;

            return expiringMessages.Add(m);
        }

        public void UpdateDisplay(string message, bool append = false)
        {
            if (cockpit != null)
            {
                cockpit.WriteText(message, append);
            }
        }

        public void LogOut(string message)
        {
            if (state == State.Setup)
                UpdateDisplay(message);
            Echo(message);
        }

        #endregion
        #region post-script
    }
}
#endregion
