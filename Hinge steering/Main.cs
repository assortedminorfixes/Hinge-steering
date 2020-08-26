#region pre-script
using System;
using System.Linq;
using System.Text;
using System.Collections;
using System.Collections.Generic;

using VRageMath;
using VRage.Game;
using VRage.Collections;
using Sandbox.ModAPI.Ingame;
using VRage.Game.Components;
using VRage.Game.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using Sandbox.Game.EntityComponents;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage.Game.ObjectBuilders.Definitions;
namespace IngameScript
{
	public class Program : MyGridProgram
	{
		#endregion
		#region in-game

		//    Blargmode's Hinge steering v1.2.0 (2020-08-26)


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
		//    It's not officially supported, but...
		//    Yes, it works. Auto straighten won't work unless you set the upper limit.
		//    Make sure you put the rotor with 0° pointing forward.


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
		//
		//    It would look like this in a name: "Hinge #steer roll"
		//    Recompile the script after changing this.




		//    == Settings == 
		//----------------------------------------------

		//    Steering Speed
		//    Default 10.
		float steeringSpeed = 10;

		//    Automatically straighten
		//    Whether steering should return to straight when releasing the turning buttons.
		//    Default true.
		bool autoStraighten = true;

		//    Tag
		//    Used in the name of a block to identify hinges and a control seat that
		//    should be controlled. Not needed if you have only one hinge and sit in the
		//    desired seat when starting the script.
		//    Default #steer
		string tag = "#steer";

		//----------------------------------------------













		// End of desciption and settings. Don't touch anything below unless you want to ;)













		// Note on the version structure:
		// v1.0.0
		// v<backwards compatabillity breaking change> . <backwards compatible feature addition> . <bugfix>

		List<Hinge> hinges;
		IMyCockpit cockpit;

		bool setupComplete = false;

		const int turnCeil = 30; // How many ticks a button is held to reach max turnig speed

		string errorMsg = "";
		int setupRetryIn = 0;

		HashSet<ExpiringMessage> expiringMessages;

		Dictionary<ControlAxis, InputCount> inputAxisCounter; // Counting how long a button has been held

		enum ControlAxis
		{
			Turn,
			Forward,
			Roll,
			Up
		}

		struct Hinge
		{
			public IMyMotorStator hinge;
			public int direction; // 1 or -1 to invert a hinge.
			public ControlAxis axis;
		}

		struct ExpiringMessage
		{
			public DateTime expiry;
			public string message;
		}

		class InputCount
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
				if (!setupComplete)
				{
					if (setupRetryIn > 0)
					{
						setupRetryIn--;
					}
					else
					{
						setupRetryIn = 5; // Retry every 5 100-tick periods.
						setupComplete = Setup();
					}
				}

				Echo(Info());
			}

			if (setupComplete && ((updateType & UpdateType.Update1) != 0) && cockpit.IsUnderControl)
			{
				foreach (var axis in inputAxisCounter)
				{
					float input = GetInput(axis.Key, cockpit);

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

				foreach (var hinge in hinges)
				{
					hinge.hinge.TargetVelocityRPM = CalcHingeVelocity(hinge);
				}
			}
		}

		float GetInput(ControlAxis axis, IMyCockpit cockpit)
		{
			switch (axis)
			{
				case ControlAxis.Forward:
					return cockpit.MoveIndicator.Z;
				case ControlAxis.Roll:
					return cockpit.RollIndicator;
				case ControlAxis.Up:
					return cockpit.MoveIndicator.Y;
			}
			return cockpit.MoveIndicator.X;
		}

		bool Setup()
		{
			var rotors = new List<IMyMotorStator>();
			GridTerminalSystem.GetBlocksOfType(rotors, x => x.IsSameConstructAs(Me));

			hinges = new List<Hinge>();

			if (rotors.Count == 0)
			{
				errorMsg = "No hinges detected.";
				return false;
			}
			else if (rotors.Count == 1)
			{
				var h = new Hinge();
				h.hinge = rotors[0];
				h.direction = (rotors[0].CustomName.ToLower().Contains("-" + tag) ? -1 : 1);
				h.axis = CheckControlAxis(rotors[0].CustomName);
				hinges.Add(h);
				if (!inputAxisCounter.ContainsKey(h.axis))
				{
					inputAxisCounter.Add(h.axis, new InputCount());
				}
			}
			else if (rotors.Count > 1)
			{
				// Select one with a tag.
				foreach (var item in rotors)
				{
					if (item.CustomName.ToLower().Contains(tag))
					{
						var h = new Hinge();
						h.hinge = item;
						h.direction = (item.CustomName.ToLower().Contains("-" + tag) ? -1 : 1);
						h.axis = CheckControlAxis(item.CustomName);
						hinges.Add(h);
						if (!inputAxisCounter.ContainsKey(h.axis))
						{
							inputAxisCounter.Add(h.axis, new InputCount());
						}
					}
				}
			}

			if (hinges.Count == 0)
			{
				errorMsg = "Could not identify hinges. Please tag the hinges you want to use by adding " + tag + " to the name of them.";
				return false;
			}

			List<IMyCockpit> cockpits = new List<IMyCockpit>();
			GridTerminalSystem.GetBlocksOfType(cockpits, x => x.IsSameConstructAs(Me));

			if (cockpits.Count == 0)
			{
				errorMsg = "No cockpit detected.";
				return false;
			}
			else
			{
				if (cockpits.Count == 1)
				{
					cockpit = cockpits[0];
				}
				else
				{
					foreach (var item in cockpits)
					{
						if (item.IsUnderControl || item.CustomName.ToLower().Contains(tag))
						{
							cockpit = item;
							break;
						}
					}
				}

				if (cockpit == null)
				{
					errorMsg = $"Could not select cockpit. Sit in one for setup to find or tag it with {tag}.";
					return false;
				}
			}

			return true;
		}

		ControlAxis CheckControlAxis(string name)
		{
			name = name.ToLower();
			if (name.Contains(tag + " forward"))
			{
				NewExpiringMessage(TimeSpan.FromSeconds(60), $"'{name}' parsed to 'forward'");
				return ControlAxis.Forward;
			}
			else if (name.Contains(tag + " roll"))
			{
				NewExpiringMessage(TimeSpan.FromSeconds(60), $"'{name}' parsed to 'roll'");
				return ControlAxis.Roll;
			}
			else if (name.Contains(tag + " up"))
			{
				NewExpiringMessage(TimeSpan.FromSeconds(60), $"'{name}' parsed to 'up'");
				return ControlAxis.Up;
			}
			else
			{
				NewExpiringMessage(TimeSpan.FromSeconds(60), $"'{name}' parsed to nothing, using 'turn'");
				return ControlAxis.Turn;
			}
		}

		float CalcHingeVelocity(Hinge hinge)
		{
			float input = GetInput(hinge.axis, cockpit);
			
			if (input == 0 && autoStraighten)
			{
				return -steeringSpeed * (1 - ((float)(inputAxisCounter[hinge.axis].lefts + inputAxisCounter[hinge.axis].rights) / turnCeil)) * (hinge.hinge.Angle / hinge.hinge.UpperLimitRad);
			}
			else if (input > 0)
			{
				return hinge.direction * -steeringSpeed * ((float)inputAxisCounter[hinge.axis].lefts / turnCeil) * Clamp(input, 0, 1);
			}
			else if (input < 0)
			{
				return hinge.direction * -steeringSpeed * ((float)inputAxisCounter[hinge.axis].rights / turnCeil) * Clamp(input, -1, 1);
			}
			return 0;
		}

		string Info()
		{
			string text = "== Blarg's Hinge Steering ==\n\n";

			if (setupComplete)
			{
				text += $"Setup complete. Script {(cockpit.IsUnderControl ? "active" : "idle")}.\nHinges: {hinges.Count}.\nSelected control seat: {cockpit.CustomName}\nAuto straighten: {(autoStraighten ? "On" : "Off")}\n\n";
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

		#endregion
		#region post-script
	}
}
#endregion