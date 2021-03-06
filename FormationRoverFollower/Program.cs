﻿using Sandbox.ModAPI.Ingame;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using VRageMath;
using VRage;
using System.Collections.Immutable;

namespace IngameScript
{
    partial class Program : MyGridProgram
    {
        // Rover Script Version 1.1
        // ============= Settings ==============
        // The id that the ship should listen to.
        // All commands not prefixed by this will be ignored.
        // Any character is allowed except ; [ ] and :
        const string followerSystemId = "System1";
        const string followerId = "Drone1";

        // The position that the ship will take relative to the main ship by default
        // In (X, Y, Z)
        // X: +Right -Left
        // Y: +Up -Down
        // Z: +Backward -Forward
        // Important: Use the command 'reset' to make the follower use this value after it has changed.
        readonly Vector3D defaultOffset = new Vector3D(50, 0, 0);

        // The name of the cockpit in the ship. You may leave this blank, but it is highly recommended	
        // to set this field to avoid unexpected behavior related to orientation.	
        // If this cockpit is not found, the script will attempt to find a suitable cockpit on its own.	
        const string cockpitName = "";

        // When true, the script will be able to see blocks that are connected via rotor, connector, etc.
        const bool useSubgridBlocks = false;

        // This allows you to automatically disable the script when the cockpit is in use.
        readonly bool autoStop = true;

        // When this is true, opon leaving the cockpit, the script will set the offset to the current position instead 
        // of returning to its designated point. Similar to the starthere command.
        // This only applies if autoStop is true.
        readonly bool autoStartHere = false;

        // This is the frequency that the script is running at. If you are experiencing lag
        // because of this script try decreasing this value. Valid values:
        // Update1 : Runs the script every tick
        // Update10 : Runs the script every 10th tick
        // Update100 : Runs the script every 100th tick
        // Note: Changing this value may cause unexpected behavior and will make following less accurate.
        readonly UpdateFrequency tickSpeed = UpdateFrequency.Update1;

        // When the tick speed of the leader is lower than the tick speed of the follower, this workaround can can be activated.
        // When this is enabled, the script will "guess" what the leader position should be in missing ticks. If the leader gets 
        // damaged and stops, the follower will keep going as if the leader is still there until you use the stop command.
        readonly bool calculateMissingTicks = true;

        // When calculateMissingTicks is enabled, the maximum number of ticks to estimate before 
        // assuming the leader is no longer active.
        // 1 game tick = 1/60 seconds
        const int maxMissingScriptTicks = 100;
        // =====================================

        // =========== Configurations ==========
        // You can save multiple offsets for your ship using the save, savehere, and load commands. The offsets for saved configurations
        // can be directly edited in the CustomData field of the programmable block. By default, the script will have a single 'default'
        // configuration with the default offset saved. When you make changes to the CustomData directly, you should recompile the script to
        // make the changes appear. CustomData will only be updated by the script when using the save and savehere commands.
        // Warning: Any error in the CustomData will cause the entire script to reset. 
        // Syntax:
        // <name1> <x1> <y1> <z1>
        // <name2> <x2> <y2> <z2>
        // =====================================

        // ============= Commands ==============
        // setoffset;x;y;z : sets the offset variable to this value
        // addoffset;x;y;z : adds these values to the current offset
        // stop : stops the script and releases control to you
        // start : starts the script after a stop command
        // starthere : starts the script in the current position
        // reset : resets and loads the default configuration with the default offset
        // clear : forces the script to forget a previous leader when calculateMissingTicks is true
        // save(;name) : saves the configuration to the current offset
        // savehere(;name) : saves the configuration to the current position
        // load;name : loads the offset from the configuration
        // =====================================

        // You can ignore any unreachable code warnings that appear in this script.

        const float P = 0.03f;
        const float I = 0;
        const float D = 0.08f;
        const float P2 = 0.03f;
        const float I2 = 0;
        const float D2 = 0.05f;

        Dictionary<string, Vector3D> configurations = new Dictionary<string, Vector3D>();
        string currentConfig = "default";

        IMyShipController rc;
        MatrixD leaderMatrix = MatrixD.Zero;
        Vector3D leaderVelocity;
        bool isDisabled = false;
        Random r = new Random();
        Vector3D offset;
        WheelControl wheels;

        bool prevControl = false;

        IMyBroadcastListener leaderListener;
        IMyBroadcastListener commandListener;
        const string transmitTag = "FSLeader" + followerSystemId;
        const string transmitCommandTag = "FSCommand" + followerSystemId;

        readonly int echoFrequency = 100;
        int runtime = 0;
        int updated = 0;
        const bool debug = false;

        public Program ()
        {
            // Prioritize the given cockpit name	
            rc = GetBlock<IMyShipController>(cockpitName, useSubgridBlocks);
            if (rc == null) // Second priority cockpit	
                rc = GetBlock<IMyCockpit>(useSubgridBlocks);
            if (rc == null) // Third priority remote control	
                rc = GetBlock<IMyRemoteControl>(useSubgridBlocks);
            if (rc == null) // No cockpits found.
                throw new Exception("No cockpit/remote control found. Set the cockpitName field in settings.");

            wheels = new WheelControl(rc, tickSpeed, GetBlocks<IMyMotorSuspension>());

            leaderListener = IGC.RegisterBroadcastListener(transmitTag);
            leaderListener.SetMessageCallback("");
            commandListener = IGC.RegisterBroadcastListener(transmitCommandTag);
            commandListener.SetMessageCallback("");

            configurations ["default"] = defaultOffset;
            offset = defaultOffset;
            LoadStorage();

            if (tickSpeed == UpdateFrequency.Update10)
                echoFrequency = 10;
            else if (tickSpeed == UpdateFrequency.Update100)
                echoFrequency = 1;
            Echo("Ready.");
        }

        void ResetMovement ()
        {
            wheels.Reset();
        }
        public void Save ()
        {
            // isDisabled;currentConfig;x;y;z
            StringBuilder sb = new StringBuilder();
            if (isDisabled)
                sb.Append("1;");
            else
                sb.Append("0;");
            sb.Append(currentConfig);
            sb.Append(';');
            sb.Append(offset.X);
            sb.Append(';');
            sb.Append(offset.Y);
            sb.Append(';');
            sb.Append(offset.Z);
            Storage = sb.ToString();
        }

        void SaveStorage ()
        {
            // Save values stored in Storage
            Save();

            // Save values stored in CustomData
            /* name1 x y z
             * name2 x y z
             */
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, Vector3D> kv in configurations)
            {
                sb.Append(kv.Key);
                sb.Append(' ');
                sb.Append(kv.Value.X);
                sb.Append(' ');
                sb.Append(kv.Value.Y);
                sb.Append(' ');
                sb.Append(kv.Value.Z);
                sb.Append('\n');
            }
            Me.CustomData = sb.ToString();
        }

        void LoadStorage ()
        {
            if (string.IsNullOrWhiteSpace(Storage) || string.IsNullOrWhiteSpace(Me.CustomData))
            {
                SaveStorage();
                Runtime.UpdateFrequency = tickSpeed;
            }

            try
            {
                // Parse CustomData values
                Dictionary<string, Vector3D> loadedConfig = new Dictionary<string, Vector3D>
                {
                    ["default"] = defaultOffset // Ensure that default offset always exists
                };
                string [] config = Me.CustomData.Split('\n');
                foreach (string s in config)
                {
                    if (string.IsNullOrWhiteSpace(s))
                        continue; // Ignore blank lines

                    string [] configValues = s.Split(' ');
                    Vector3D value = new Vector3D(
                        double.Parse(configValues [1]),
                        double.Parse(configValues [2]),
                        double.Parse(configValues [3])
                        );
                    loadedConfig [configValues [0]] = value;
                }

                // Parse Storage values
                string [] args = Storage.Split(';');
                bool loadedIsDisabled = args [0] == "1";
                string loadedCurrentConfig = args [1];
                Vector3D loadedOffset = new Vector3D(
                    double.Parse(args [2]),
                    double.Parse(args [3]),
                    double.Parse(args [4])
                    );

                // Parse successful, update the real values.
                configurations = loadedConfig;
                currentConfig = loadedCurrentConfig;
                if (configurations.ContainsKey(currentConfig))
                    currentConfig = "default"; // If something went wrong, use the only guaranteed configuration.
                offset = loadedOffset;
                isDisabled = loadedIsDisabled;
                if (!isDisabled)
                    Runtime.UpdateFrequency = tickSpeed;
            } catch (Exception)
            {
                SaveStorage();
                Runtime.UpdateFrequency = tickSpeed;
                //throw;
            }
        }

        public void Main (string argument, UpdateType updateSource)
        {
            if (updateSource == UpdateType.Update100 || updateSource == UpdateType.Update10 || updateSource == UpdateType.Update1)
            {
                if (debug || runtime % echoFrequency == 0)
                    WriteEcho();

                if (rc.GetNaturalGravity() == Vector3.Zero)
                {
                    ResetMovement();
                    throw new Exception("Panic! No planet detected!");
                }

                // Check to make sure that a message from the leader has been received
                if (leaderMatrix == MatrixD.Zero)
                {
                    runtime++;
                    return;
                }

                if (autoStop)
                {
                    bool control = rc.IsUnderControl;
                    if (control != prevControl)
                    {
                        if (control)
                            ResetMovement();
                        else if (autoStartHere)
                            offset = CurrentOffset();

                        prevControl = control;
                    }

                    if (prevControl)
                    {
                        runtime++;
                        return;
                    }
                }

                Move();
                runtime++;
            }
            else if (updateSource == UpdateType.IGC)
            {
                if (leaderListener.HasPendingMessage)
                {
                    var data = leaderListener.AcceptMessage().Data;
                    if (data is MyTuple<MatrixD, Vector3D, long>)
                    {
                        // Format: leader data, leader velocity, source grid id
                        MyTuple<MatrixD, Vector3D, long> msg = (MyTuple<MatrixD, Vector3D, long>)data;
                        if (msg.Item3 != Me.CubeGrid.EntityId)
                        {
                            leaderMatrix = msg.Item1;
                            leaderVelocity = msg.Item2;
                            updated = runtime;
                        }
                        else
                        {
                            leaderMatrix = MatrixD.Zero;
                        }
                    }
                    else if (data is MyTuple<MatrixD, Vector3D>)
                    {
                        // Format: leader data, leader velocity
                        MyTuple<MatrixD, Vector3D> msg = (MyTuple<MatrixD, Vector3D>)data;
                        leaderMatrix = msg.Item1;
                        leaderVelocity = msg.Item2;
                        updated = runtime;
                    }
                }

                if (commandListener.HasPendingMessage)
                {
                    var data = commandListener.AcceptMessage().Data;
                    if (data is MyTuple<string, string>)
                    {
                        MyTuple<string, string> msg = (MyTuple<string, string>)data;

                        if (msg.Item1.Length > 0)
                        {
                            foreach (string s in msg.Item1.Split(';'))
                            {
                                if (s == followerId)
                                {
                                    RemoteCommand(msg.Item2);
                                    return;
                                }
                            }
                            return;
                        }
                        else
                        {
                            RemoteCommand(msg.Item2);
                            return;
                        }
                    }
                }
            }
            else
            {
                RemoteCommand(argument);
            }
        }
        Vector3D CurrentOffset ()
        {
            return Vector3D.TransformNormal(rc.GetPosition() - leaderMatrix.Translation, MatrixD.Transpose(rc.WorldMatrix));
        }

        void WriteEcho ()
        {
            Echo("Running.\nConfigs:");
            foreach (string s in configurations.Keys)
            {
                if (s == currentConfig)
                    Echo(s + '*');
                else
                    Echo(s);
            }
            Echo(offset.ToString("0.00"));
            if (leaderMatrix == MatrixD.Zero)
                Echo("No messages received.");
            else if (calculateMissingTicks && runtime - updated > maxMissingScriptTicks)
                Echo($"Weak signal, message received {runtime - updated} ticks ago.");
            if (autoStop && prevControl)
                Echo("Cockpit is under control.");
        }

        void Move ()
        {
            // Apply translations to find the world position that this follower is supposed to be

            Vector3D targetPosition = Vector3D.Transform(offset, leaderMatrix);

            if (calculateMissingTicks)
            {
                int diff = Math.Min(Math.Abs(runtime - updated), maxMissingScriptTicks);
                if (diff > 0)
                {
                    double secPerTick = 1.0 / 60;
                    if (tickSpeed == UpdateFrequency.Update10)
                        secPerTick = 1.0 / 6;
                    else if (tickSpeed == UpdateFrequency.Update100)
                        secPerTick = 5.0 / 3;
                    double secPassed = diff * secPerTick;
                    targetPosition += leaderVelocity * secPassed;
                }
            }
            
            wheels.Update(targetPosition);
        }
        
        void RemoteCommand (string command)
        {
            string [] args = command.Split(';');

            switch (args [0])
            {
                case "setoffset": // setoffset;x;y;z
                    if (args.Length == 4)
                    {
                        if (args [1] == "")
                            args [1] = this.offset.X.ToString();
                        if (args [2] == "")
                            args [2] = this.offset.Y.ToString();
                        if (args [3] == "")
                            args [3] = this.offset.Z.ToString();

                        Vector3D offset;
                        if (!StringToVector(args [1], args [2], args [3], out offset))
                            return;
                        this.offset = offset;
                        WriteEcho();
                    }
                    else
                    {
                        return;
                    }
                    break;
                case "addoffset": // addoffset;x;y;z
                    if (args.Length == 4)
                    {
                        Vector3D offset;
                        if (!StringToVector(args [1], args [2], args [3], out offset))
                            return;
                        this.offset += offset;
                        WriteEcho();
                    }
                    else
                    {
                        return;
                    }
                    break;
                case "stop": // stop
                    Runtime.UpdateFrequency = UpdateFrequency.None;
                    ResetMovement();
                    isDisabled = true;
                    Echo("Stopped.");
                    break;
                case "start": // start
                    Runtime.UpdateFrequency = tickSpeed;
                    isDisabled = false;
                    WriteEcho();
                    break;
                case "starthere": // starthere
                    Runtime.UpdateFrequency = tickSpeed;
                    offset = CurrentOffset();
                    isDisabled = false;
                    WriteEcho();
                    break;
                case "reset": // reset
                    offset = defaultOffset;
                    configurations ["default"] = defaultOffset;
                    currentConfig = "default";
                    isDisabled = false;
                    SaveStorage();
                    WriteEcho();
                    break;
                case "save": // save(;name)
                    {
                        string key = currentConfig;
                        if (args.Length > 1)
                            key = args [1];

                        if (key.Contains(' '))
                            return;

                        configurations [key] = offset;
                        SaveStorage();
                    }
                    break;
                case "savehere": // save(;name)
                    {
                        string key = currentConfig;
                        if (args.Length > 1)
                            key = args [1];

                        if (key.Contains(' '))
                            return;

                        Vector3D newOffset = CurrentOffset();
                        configurations [key] = newOffset;
                        SaveStorage();
                    }
                    break;
                case "load": // load;name
                    if (args.Length == 1 || !configurations.ContainsKey(args [1]))
                        return;
                    // Load the new config
                    offset = configurations [args [1]];
                    currentConfig = args [1];
                    isDisabled = false;
                    WriteEcho();
                    break;
                case "clear":
                    leaderMatrix = MatrixD.Zero;
                    break;
            }
        }

        bool StringToVector (string x, string y, string z, out Vector3D output)
        {
            try
            {
                double x2 = double.Parse(x);
                double y2 = double.Parse(y);
                double z2 = double.Parse(z);
                output = new Vector3D(x2, y2, z2);
                return true;
            } 
            catch (Exception)
            {
                output = new Vector3D();
                return false;
            }
        }


    }
}