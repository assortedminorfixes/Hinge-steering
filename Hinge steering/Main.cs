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

		//    Blargmode's Hinge steering v1.0.0 (2020-08-22)


		//    == Description ==
		//    This script let you create wheel loader like vehicles that turn by
		//    bending on the middle.


		//    == How to install ==
		//    Add to programmable block.
		//    Check if it managed to set itself up. If not, it will tell you what's
		//    wrong in the script output.


		//    == Turning the wrong way? ==
		//    Use the tag on the hinge with a minus sign in front, like this:
		//    -#steer
		//    Then recompile. That inverts the steering on that hinge.


		//    == How many hinges? ==
		//    There's no limit in the code. It's probably wise to only have one 
		//    controlled hinge per joint. If you have more than one, set the braking
		//    force to 0 on the others.


		//    == Does it work with rotors? ==
		//    It's not officially supported, but...
		//    Yes, it works. Auto straighten won't work unless you set the upper limit.
		//    Make sure you put the rotor with 0° pointing forward.




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















		List<Hinge> hinges;
		IMyCockpit cockpit;

		bool setupComplete = false;

		const int turnCeil = 30; // How many ticks a button is held to reach max turnig speed
		int lefts = 0; // Couting how long the left button has been held in ticks.
		int rights = 0; // Same but right button.

		string errorMsg = "";
		int setupRetryIn = 0;

		struct Hinge
		{
			public IMyMotorStator hinge;
			public int direction; // 1 or -1 to invert a hinge.
		}

		public Program()
		{
			Runtime.UpdateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update100;
		}

		public void Main(string argument, UpdateType updateType)
		{

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
				if (cockpit.MoveIndicator.X > 0)
				{
					if (lefts <= turnCeil) lefts++;
					rights = 0;
				}
				else if (cockpit.MoveIndicator.X < 0)
				{
					if (rights <= turnCeil) rights++;
					lefts = 0;
				}
				else // if blabla.X == 0
				{
					if (lefts > 0) lefts--;
					if (rights > 0) rights--;
				}

				foreach (var hinge in hinges)
				{
					hinge.hinge.TargetVelocityRPM = CalcHingeVelocity(hinge);
				}
			}
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
				hinges.Add(h);
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
						hinges.Add(h);
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

		float CalcHingeVelocity(Hinge hinge)
		{
			if (cockpit.MoveIndicator.X == 0 && autoStraighten)
			{
				return -steeringSpeed * (1 - ((float)(lefts + rights) / turnCeil)) * (hinge.hinge.Angle / hinge.hinge.UpperLimitRad);
			}
			else if (cockpit.MoveIndicator.X > 0)
			{
				return hinge.direction * -steeringSpeed * ((float)lefts / turnCeil) * Clamp(cockpit.MoveIndicator.X, 0, 1);
			}
			else if (cockpit.MoveIndicator.X < 0)
			{
				return hinge.direction * -steeringSpeed * ((float)rights / turnCeil) * Clamp(cockpit.MoveIndicator.X, -1, 1);
			}
			return 0;
		}

		string Info()
		{
			string text = "== Blarg's Hinge Steering ==\n\n";

			if (setupComplete)
			{
				text += $"Setup complete. Script {(cockpit.IsUnderControl ? "active" : "idle")}.\nHinges: {hinges.Count}.\nSelected control seat: {cockpit.CustomName}";
			}
			else
			{
				if (setupRetryIn > 0)
				{
					text += $"Setup failed.\n\n{errorMsg}\n\nRetrying in {setupRetryIn} long seconds.";
				}
				else
				{
					text += "Running setup...";
				}
			}

			return text;
		}

		public static float Clamp(float value, float min, float max)
		{
			return (value < min) ? min : (value > max) ? max : value;
		}

		#endregion
		#region post-script
	}
}
#endregion