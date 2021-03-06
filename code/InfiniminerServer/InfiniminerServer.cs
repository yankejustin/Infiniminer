﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Lidgren.Network;
using Lidgren.Network.Xna;
using Microsoft.Xna.Framework;

namespace Infiniminer
{
    public class InfiniminerServer
    {
        InfiniminerNetServer netServer = null;
        public BlockType[, ,] blockList = null;    // In game coordinates, where Y points up.
        public Int32[, , ,] blockListContent = null;
        public Int32[, ,] blockListHP = null;
        public bool[,,] allowBlock = null;
        public Int32[,] ResearchComplete = null;
        public Int32[,] ResearchProgress = null;
        public Int32[,] artifactActive = null;
        PlayerTeam[, ,] blockCreatorTeam = null;

        Dictionary<PlayerTeam, PlayerBase> basePosition = new Dictionary<PlayerTeam, PlayerBase>();
        PlayerBase RedBase;
        PlayerBase BlueBase;
        public TimeSpan[] serverTime = { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };
        public int timeQueue = 0;
        float delta;
        public DateTime lastTime;
        public int artifactCost = 1;
        public int MAPSIZE = 64;
        Thread physics;
        //Thread mechanics;
        Dictionary<NetConnection, Player> playerList = new Dictionary<NetConnection, Player>();
        bool sleeping = true;
        int lavaBlockCount = 0;
        int waterBlockCount = 0;
        uint oreFactor = 10;
        int frameCount = 100;
        uint prevMaxPlayers = 16;
        bool includeLava = true;
        bool includeWater = true;
        bool physicsEnabled = false;

        string levelToLoad = "";
        string greeter = "";
        List<NetConnection> toGreet = new List<NetConnection>();
        Dictionary<string, short> admins = new Dictionary<string, short>(); //Short represents power - 1 for mod, 2 for full admin

        bool[,,] tntExplosionPattern = new bool[0,0,0];
        bool announceChanges = true;

        DateTime lastServerListUpdate = DateTime.Now;
        DateTime lastMapBackup = DateTime.Now;
        List<string> banList = null;

        const int CONSOLE_SIZE = 30;
        List<string> consoleText = new List<string>();
        string consoleInput = "";

        bool keepRunning = true;

        uint teamCashRed = 0;
        uint teamCashBlue = 0;
        uint teamOreRed = 0;
        uint teamOreBlue = 0;
        uint teamArtifactsRed = 0;
        uint teamArtifactsBlue = 0;
        int[] teamRegeneration = { 0, 0, 0 };
        uint winningCashAmount = 6;

        PlayerTeam winningTeam = PlayerTeam.None;

        bool[, ,] flowSleep = new bool[64, 64, 64]; //if true, do not calculate this turn

        // Server restarting variables.
        DateTime restartTime = DateTime.Now;
        bool restartTriggered = false;
        
        //Variable handling
        Dictionary<string,bool> varBoolBindings = new Dictionary<string, bool>();
        Dictionary<string,string> varStringBindings = new Dictionary<string, string>();
        Dictionary<string, int> varIntBindings = new Dictionary<string, int>();
        Dictionary<string,string> varDescriptions = new Dictionary<string,string>();
        Dictionary<string, bool> varAreMessage = new Dictionary<string, bool>();

        public void DropItem(Player player, uint ID)
        {
            if (player.Alive)
            {
                if (ID == 1 && player.Ore > 19)
                {
                    uint it = SetItem(ItemType.Ore, player.Position, player.Heading, player.Heading, PlayerTeam.None, 0);
                    itemList[it].Content[5] += (int)Math.Floor((double)(player.Ore) / 20) - 1;
                    itemList[it].Scale = 0.5f + (float)(itemList[it].Content[5]) * 0.1f;
                    SendItemScaleUpdate(itemList[it]);

                    player.Ore = 0;
                    SendOreUpdate(player);
                }
                else if (ID == 2 && player.Cash > 9)
                {
                    uint it = SetItem(ItemType.Gold, player.Position, player.Heading, player.Heading, PlayerTeam.None, 0);
                    player.Cash -= 10;
                    player.Weight--;

                    while (player.Cash > 9)
                    {
                        itemList[it].Content[5] += 1;
                        player.Cash -= 10;
                        player.Weight--;
                    }
                    itemList[it].Scale = 0.5f + (float)(itemList[it].Content[5]) * 0.1f;
                    SendItemScaleUpdate(itemList[it]);
                    SendCashUpdate(player);
                    SendWeightUpdate(player);
                }
                else if (ID == 3 && player.Content[10] > 0)//artifact drop
                {
                    uint it = SetItem(ItemType.Artifact, player.Position, player.Heading, player.Heading, PlayerTeam.None, player.Content[10]);
                    itemList[it].Content[10] = player.Content[10];//setting artifacts ID

                    player.Content[10] = 0;//artifact slot
                    SendContentSpecificUpdate(player, 10);//tell player he has no artifact now
                }
                else if (ID == 4 && player.Content[11] > 0)//diamond
                {
                    while (player.Content[11] > 0)
                    {
                        uint it = SetItem(ItemType.Diamond, player.Position, player.Heading, player.Heading, PlayerTeam.None, 0);
                        player.Content[11]--;
                        player.Weight--;
                    }

                    SendContentSpecificUpdate(player, 11);
                    SendWeightUpdate(player);
                }
            }
        }

        public void Player_Dead(Player player, string reason)
        {
            if (reason != "")
            {
                NetBuffer msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);
                msgBuffer.Write((byte)(player.Team == PlayerTeam.Red ? ChatMessageType.SayRedTeam : ChatMessageType.SayBlueTeam));
                msgBuffer.Write(player.Handle + " " + reason);
                foreach (NetConnection netConn in playerList.Keys)
                    if (netConn.Status == NetConnectionStatus.Connected)
                        netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder3);
            }

            ConsoleWrite("PLAYER_DEAD: " + player.Handle + " DROPPED:" + player.Ore + " ore/" + player.Cash + " gold!");

            if (player.Ore > 19)
            {
                uint it = SetItem(ItemType.Ore, player.Position, Vector3.Zero, new Vector3((float)(randGen.NextDouble() - 0.5) * 2, (float)(randGen.NextDouble() * 1.5), (float)(randGen.NextDouble() - 0.5) * 2), PlayerTeam.None, 0);
                itemList[it].Content[5] += (int)Math.Floor((double)(player.Ore) / 20)-1;
                itemList[it].Scale = 0.5f + (float)(itemList[it].Content[5]) * 0.1f;
                SendItemScaleUpdate(itemList[it]);
            }

            if (player.Cash > 9)
            {
                uint it = SetItem(ItemType.Gold, player.Position, Vector3.Zero, new Vector3((float)(randGen.NextDouble() - 0.5) * 2, (float)(randGen.NextDouble() * 1.5), (float)(randGen.NextDouble() - 0.5) * 2), PlayerTeam.None, 0);
                itemList[it].Content[5] += (int)Math.Floor((double)(player.Cash) / 10) - 1;
                itemList[it].Scale = 0.5f + (float)(itemList[it].Content[5]) * 0.1f;
                SendItemScaleUpdate(itemList[it]);
            }

            if (player.Content[10] > 0)
            {
                uint it = SetItem(ItemType.Artifact, player.Position, Vector3.Zero, new Vector3((float)(randGen.NextDouble() - 0.5) * 2, (float)(randGen.NextDouble() * 1.5), (float)(randGen.NextDouble() - 0.5) * 2), PlayerTeam.None, player.Content[10]);
                itemList[it].Content[10] = player.Content[10];//setting artifacts ID

                player.Content[10] = 0;//artifact slot

                SendContentSpecificUpdate(player, 10);//tell player he has no artifact now
            }

            while (player.Content[11] > 0)
            {
                uint it = SetItem(ItemType.Diamond, player.Position, Vector3.Zero, new Vector3((float)(randGen.NextDouble() - 0.5) * 2, (float)(randGen.NextDouble() * 1.5), (float)(randGen.NextDouble() - 0.5) * 2), PlayerTeam.None, 0);
                player.Content[11]--;
            }

            if (player.Class == PlayerClass.Prospector && player.Content[5] > 0)
            {
                player.Content[5] = 0;
                SendPlayerContentUpdate(player, 5);
            }
            player.Ore = 0;
            player.Cash = 0;//gold
            player.Weight = 0;
            player.Health = 0;
            
            player.Content[2] = 0;
            player.Content[3] = 0;
            player.Content[4] = 0;
            player.Content[5] = 0;//ability slot
            player.Content[11] = 0;//diamond slots
            SendContentSpecificUpdate(player, 11);
           

            player.rSpeedCount = 0;

            if (player.Alive == true)//avoid sending multiple death threats
            {
                player.Alive = false;
                SendResourceUpdate(player);
                SendPlayerDead(player);
            }
            
            if (player.HealthMax > 0 && player.Team != PlayerTeam.None)
            {
                SendPlayerRespawn(player);//allow this player to instantly respawn
            }
        }

        public void Auth_Refuse(Player pl)
        {
            if (pl.rTime < DateTime.Now)
            {
                pl.rTime = DateTime.Now + TimeSpan.FromSeconds(1);

                if (pl.rUpdateCount > 25)//20 is easily pushed while moving and triggering levers
                {
                    ConsoleWrite("PLAYER_DEAD_UPDATE_FLOOD: " + pl.Handle + "@" + pl.rUpdateCount + " ROUNDTRIP MS:" + pl.NetConn.AverageRoundtripTime*1000);
                    Player_Dead(pl, "WOUND HIS CLOCK TOO HIGH!");
                    
                }
                else if(pl.rSpeedCount > 10.0f && pl.Alive && pl.respawnTimer < DateTime.Now)//7
                {
                    ConsoleWrite("PLAYER_DEAD_TOO_FAST: " + pl.Handle + "@"+pl.rSpeedCount+" ROUNDTRIP MS:" + pl.NetConn.AverageRoundtripTime*1000);
                    Player_Dead(pl, "WAS SPAMMING!");
                }
                else if (pl.rCount > 10 && pl.Alive)
                {
                    ConsoleWrite("PLAYER_DEAD_ILLEGAL_MOVEMENT: " + pl.Handle + "@" + pl.rCount + " ROUNDTRIP MS:" + pl.NetConn.AverageRoundtripTime*1000);
                    Player_Dead(pl, "HAD A FEW TOO MANY CLOSE CALLS!");
                }
                pl.rCount = 0;
                pl.rUpdateCount = 0;
                pl.rSpeedCount = 0;
            }
        }

        public double Dist_Auth(Vector3 x, Vector3 y)
        {
            float dx = y.X - x.X;
            float dz = y.Z - x.Z;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            return dist;
        }

        public Vector3 Auth_Position(Vector3 pos,Player pl, bool check)//check boundaries and legality of action
        {
            BlockType testpoint = BlockAtPoint(pos);

            if (testpoint == BlockType.None || testpoint == BlockType.Fire || testpoint == BlockType.Vacuum || testpoint == BlockType.Water || testpoint == BlockType.Lava || testpoint == BlockType.StealthBlockB && pl.Team == PlayerTeam.Blue || testpoint == BlockType.TransBlue && pl.Team == PlayerTeam.Blue || testpoint == BlockType.StealthBlockR && pl.Team == PlayerTeam.Red || testpoint == BlockType.TransRed && pl.Team == PlayerTeam.Red)
            {//check if player is not in wall
               //falldamage

                //if (testpoint == BlockType.Fire)
                //{
                //    //burn
                //    if (pl.Health > 1)
                //    {
                //        pl.Health = pl.Health - 10;
                //        if (pl.Health == 0)
                //        {
                //            pl.Weight = 0;
                //            pl.Alive = false;

                //            SendResourceUpdate(pl);
                //            SendPlayerDead(pl);
                //            ConsoleWrite(pl.Handle + " died in the fire.");
                //        }
                //    }
                //}
                if (check)
                {
                    pl.rSpeedCount += Dist_Auth(pos, pl.Position);
                    pl.rUpdateCount += 1;
                }
                Auth_Refuse(pl);
            }
            else
            {
                if (pl.Alive)
                {
                    
                    //pl.Ore = 0;//should be calling death function for player
                    //pl.Cash = 0;
                    //pl.Weight = 0;
                    //pl.Health = 0;
                    //pl.Alive = false;

                    //SendResourceUpdate(pl);
                    //SendPlayerDead(pl);

                   // ConsoleWrite("refused" + pl.Handle + " " + pos.X + "/" + pos.Y + "/" + pos.Z);
                    ushort x = (ushort)pos.X;
                    ushort y = (ushort)pos.Y;
                    ushort z = (ushort)pos.Z;

                    
                    if (x < 0 || y < 0 || z < 0 || x >= MAPSIZE || y >= MAPSIZE || z >= MAPSIZE)
                    {
                        Auth_Refuse(pl);
                        pl.rCount += 1;
                        return pl.Position;
                    }

                    if (testpoint == BlockType.TrapB)
                        if (pl.Team == PlayerTeam.Red)//destroy trap block
                        {
                            pl.rSpeedCount += Dist_Auth(pos, pl.Position);
                            pl.rUpdateCount += 1;

                            Auth_Refuse(pl);
                            SetBlockDebris(x, y, z, BlockType.None, PlayerTeam.None);
                            return (pos);
                        }
                    else if (testpoint == BlockType.TrapR)
                        if (pl.Team == PlayerTeam.Blue)//destroy trap block
                        {
                            pl.rSpeedCount += Dist_Auth(pos, pl.Position);
                            pl.rUpdateCount += 1;

                            Auth_Refuse(pl);
                            SetBlockDebris(x, y, z, BlockType.None, PlayerTeam.None);
                            return (pos);
                        }

                    SetBlockForPlayer(x, y, z, blockList[x, y, z], blockCreatorTeam[x, y, z], pl);
                    Auth_Refuse(pl);
                    pl.rCount += 1;

                    return pl.Position;
                }
                else//player is dead, return position silent
                {
                    return pl.Position;
                }
            }

            //if (Distf(pl.Position, pos) > 0.35)
            //{   //check that players last update is not further than it should be
            //    ConsoleWrite("refused" + pl.Handle + " speed:" + Distf(pl.Position, pos));
            //    //should call force update player position
            //    return pos;// pl.Position;
            //}
            //else
            //{
            //    return pos;
            //}

            return pos;
        }
        public Vector3 Auth_Heading(Vector3 head)//check boundaries and legality of action
        {
            return head;
        }

        public void varBindingsInitialize()
        {
            //Bool bindings
            varBind("tnt", "TNT explosions", false, true);
            varBind("stnt", "Spherical TNT explosions", true, true);
            varBind("sspreads", "Lava spreading via shock blocks", true, false);
            varBind("roadabsorbs", "Letting road blocks above lava absorb it", true, false);
            varBind("insane", "Insane liquid spreading, so as to fill any hole", false, false);
            varBind("minelava", "Lava pickaxe mining", true, false);
            //***New***
            varBind("public", "Server publicity", true, false);
            varBind("sandbox", "Sandbox mode", true, false);
            //Announcing is a special case, as it will never announce for key name announcechanges
            varBind("announcechanges", "Toggles variable changes being announced to clients", true, false);

            //String bindings
            varBind("name", "Server name as it appears on the server browser", "Unnamed Server");
            varBind("greeter", "The message sent to new players", "");

            //Int bindings
            varBind("maxplayers", "Maximum player count", 16);
            varBind("explosionradius", "The radius of spherical tnt explosions", 3);
        }

        public void varBind(string name, string desc, bool initVal, bool useAre)
        {
            varBoolBindings[name] = initVal;
            varDescriptions[name] = desc;
            /*if (varBoolBindings.ContainsKey(name))
                varBoolBindings[name] = initVal;
            else
                varBoolBindings.Add(name, initVal);

            if (varDescriptions.ContainsKey(name))
                varDescriptions[name] = desc;
            else
                varDescriptions.Add(name, desc);*/

            varAreMessage[name] = useAre;
        }

        public void varBind(string name, string desc, string initVal)
        {
            varStringBindings[name] = initVal;
            varDescriptions[name] = desc;
            /*
            if (varStringBindings.ContainsKey(name))
                varStringBindings[name] = initVal;
            else
                varStringBindings.Add(name, initVal);

            if (varDescriptions.ContainsKey(name))
                varDescriptions[name] = desc;
            else
                varDescriptions.Add(name, desc);*/
        }

        public void varBind(string name, string desc, int initVal)
        {
            varIntBindings[name] = initVal;
            varDescriptions[name] = desc;
            /*if (varDescriptions.ContainsKey(name))
                varDescriptions[name] = desc;
            else
                varDescriptions.Add(name, desc);*/
        }

        public bool varChangeCheckSpecial(string name)
        {
            switch (name)
            {
                case "maxplayers":
                    //Check if smaller than player count
                    if (varGetI(name) < playerList.Count)
                    {
                        //Bail, set to previous value
                        varSet(name, (int)prevMaxPlayers,true);
                        return false;
                    }
                    else
                    {
                        prevMaxPlayers = (uint)varGetI(name);
                        netServer.Configuration.MaxConnections = varGetI(name);
                    }
                    break;
                case "explosionradius":
                    CalculateExplosionPattern();
                    break;
                case "greeter":
                    /*PropertyBag _P = new PropertyBag(new InfiniminerGame(new string[]{}));
                    string[] format = _P.ApplyWordrwap(varGetS("greeter"));
                    */
                    greeter = varGetS("greeter");
                    break;
            }
            return true;
        }

        public bool varGetB(string name)
        {
            if (varBoolBindings.ContainsKey(name) && varDescriptions.ContainsKey(name))
                return varBoolBindings[name];
            else
                return false;
        }

        public string varGetS(string name)
        {
            if (varStringBindings.ContainsKey(name) && varDescriptions.ContainsKey(name))
                return varStringBindings[name];
            else
                return "";
        }

        public int varGetI(string name)
        {
            if (varIntBindings.ContainsKey(name) && varDescriptions.ContainsKey(name))
                return varIntBindings[name];
            else
                return -1;
        }

        public int varExists(string name)
        {
            if (varDescriptions.ContainsKey(name))
                if (varBoolBindings.ContainsKey(name))
                    return 1;
                else if (varStringBindings.ContainsKey(name))
                    return 2;
                else if (varIntBindings.ContainsKey(name))
                    return 3;
            return 0;
        }

        public void varSet(string name, bool val)
        {
            varSet(name, val, false);
        }

        public void varSet(string name, bool val, bool silent)
        {
            if (varBoolBindings.ContainsKey(name) && varDescriptions.ContainsKey(name))
            {
                varBoolBindings[name] = val;
                string enabled = val ? "enabled!" : "disabled.";
                if (name!="announcechanges"&&!silent)
                    MessageAll(varDescriptions[name] + (varAreMessage[name] ? " are " + enabled : " is " + enabled));
                if (!silent)
                {
                    varReportStatus(name, false);
                    varChangeCheckSpecial(name);
                }
            }
            else
                ConsoleWrite("Variable \"" + name + "\" does not exist!");
        }

        public void varSet(string name, string val)
        {
            varSet(name, val, false);
        }

        public void varSet(string name, string val, bool silent)
        {
            if (varStringBindings.ContainsKey(name) && varDescriptions.ContainsKey(name))
            {
                varStringBindings[name] = val;
                if (!silent)
                {
                    varReportStatus(name);
                    varChangeCheckSpecial(name);
                }
            }
            else
                ConsoleWrite("Variable \"" + name + "\" does not exist!");
        }

        public void varSet(string name, int val)
        {
            varSet(name, val, false);
        }

        public void varSet(string name, int val, bool silent)
        {
            if (varIntBindings.ContainsKey(name) && varDescriptions.ContainsKey(name))
            {
                varIntBindings[name] = val;
                if (!silent)
                {
                    MessageAll(name + " = " + val.ToString());
                    varChangeCheckSpecial(name);
                }
            }
            else
                ConsoleWrite("Variable \"" + name + "\" does not exist!");
        }

        public string varList()
        {
            return varList(false);
        }

        private void varListType(ICollection<string> keys, string naming)
        {
            
            const int lineLength = 3;
            if (keys.Count > 0)
            {
                ConsoleWrite(naming);
                int i = 1;
                string output = "";
                foreach (string key in keys)
                {
                    if (i == 1)
                    {
                        output += "\t" + key;
                    }
                    else if (i >= lineLength)
                    {
                        output += ", " + key;
                        ConsoleWrite(output);
                        output = "";
                        i = 0;
                    }
                    else
                    {
                        output += ", " + key;
                    }
                    i++;
                }
                if (i > 1)
                    ConsoleWrite(output);
            }
        }

        public string varList(bool autoOut)
        {
            if (!autoOut)
            {
                string output = "";
                int i = 0;
                foreach (string key in varBoolBindings.Keys)
                {
                    if (i == 0)
                        output += key;
                    else
                        output += "," + key;
                    i++;
                }
                foreach (string key in varStringBindings.Keys)
                {
                    if (i == 0)
                        output += "s " + key;
                    else
                        output += ",s " + key;
                    i++;
                }
                return output;
            }
            else
            {
                varListType((ICollection<string>)varBoolBindings.Keys, "Boolean Vars:");
                varListType((ICollection<string>)varStringBindings.Keys, "String Vars:");
                varListType((ICollection<string>)varIntBindings.Keys, "Int Vars:");

                /*ConsoleWrite("String count: " + varStringBindings.Keys.Count);
                outt = new string[varStringBindings.Keys.Count];
                varStringBindings.Keys.CopyTo(outt, 0);
                varListType(outt, "String Vars:");

                ConsoleWrite("Int count: " + varIntBindings.Keys.Count);
                outt = new string[varIntBindings.Keys.Count];
                varIntBindings.Keys.CopyTo(outt, 0);
                varListType(outt, "Integer Vars:");*/
                /*if (varStringBindings.Count > 0)
                {
                    ConsoleWrite("String Vars:");
                    int i = 1;
                    string output = "";
                    foreach (string key in varStringBindings.Keys)
                    {
                        if (i == 1)
                        {
                            output += key;
                        }
                        else if (i >= lineLength)
                        {
                            output += "," + key;
                            ConsoleWrite(output);
                            output = "";
                        }
                        else
                        {
                            output += "," + key;
                        }
                        i++;
                    }
                }
                if (varIntBindings.Count > 0)
                {
                    ConsoleWrite("Integer Vars:");
                    int i = 1;
                    string output = "";
                    foreach (string key in varIntBindings.Keys)
                    {
                        if (i == 1)
                        {
                            output += "\t"+key;
                        }
                        else if (i >= lineLength)
                        {
                            output += "," + key;
                            ConsoleWrite(output);
                            output = "";
                        }
                        else
                        {
                            output += "," + key;
                        }
                        i++;
                    }
                }*/
                return "";
            }
        }

        public void varReportStatus(string name)
        {
            varReportStatus(name, true);
        }

        public void varReportStatus(string name, bool full)
        {
            if (varDescriptions.ContainsKey(name))
            {
                if (varBoolBindings.ContainsKey(name))
                {
                    ConsoleWrite(name + " = " + varBoolBindings[name].ToString());
                    if (full)
                        ConsoleWrite(varDescriptions[name]);
                    return;
                }
                else if (varStringBindings.ContainsKey(name))
                {
                    ConsoleWrite(name + " = " + varStringBindings[name]);
                    if (full)
                        ConsoleWrite(varDescriptions[name]);
                    return;
                }
                else if (varIntBindings.ContainsKey(name))
                {
                    ConsoleWrite(name + " = " + varIntBindings[name]);
                    if (full)
                        ConsoleWrite(varDescriptions[name]);
                    return;
                }
            }
            ConsoleWrite("Variable \"" + name + "\" does not exist!");
        }

        public string varReportStatusString(string name, bool full)
        {
            if (varDescriptions.ContainsKey(name))
            {
                if (varBoolBindings.ContainsKey(name))
                {
                    return name + " = " + varBoolBindings[name].ToString();
                }
                else if (varStringBindings.ContainsKey(name))
                {
                    return name + " = " + varStringBindings[name];
                }
                else if (varIntBindings.ContainsKey(name))
                {
                    return name + " = " + varIntBindings[name];
                }
            }
            return "";
        }

        public InfiniminerServer()
        {
            Console.SetWindowSize(1, 1);
            Console.SetBufferSize(80, CONSOLE_SIZE + 4);
            Console.SetWindowSize(80, CONSOLE_SIZE + 4);

            physics = new Thread(new ThreadStart(this.DoPhysics));
            physics.Priority = ThreadPriority.Normal;
            physics.Start();

            //mechanics = new Thread(new ThreadStart(this.DoMechanics));
            //mechanics.Priority = ThreadPriority.AboveNormal;
            //mechanics.Start();
        }

        public string GetExtraInfo()
        {
            string extraInfo = "";
            if (varGetB("sandbox"))
                extraInfo += "sandbox";
            else
                extraInfo += string.Format("{0:#.##k}", winningCashAmount / 1000);
            if (!includeLava)
                extraInfo += ", !lava";
            if (!includeWater)
                extraInfo += ", !water";
            if (!varGetB("tnt"))
                extraInfo += ", !tnt";
            if (varGetB("insane") || varGetB("sspreads") || varGetB("stnt"))
                extraInfo += ", insane";

/*            if (varGetB("insanelava"))//insaneLava)
                extraInfo += ", ~lava";
            if (varGetB("sspreads"))
                extraInfo += ", shock->lava";
            if (varGetB("stnt"))//sphericalTnt && false)
                extraInfo += ", stnt";*/
            return extraInfo;
        }

        public void PublicServerListUpdate()
        {
            PublicServerListUpdate(false);
        }

        public void PublicServerListUpdate(bool doIt)
        {
            if (!varGetB("public"))
                return;

            TimeSpan updateTimeSpan = DateTime.Now - lastServerListUpdate;
            if (updateTimeSpan.TotalMinutes >= 1 || doIt)
                CommitUpdate();
        }

        public bool ProcessCommand(string chat)
        {
            return ProcessCommand(chat, (short)1, null);
        }

        public bool ProcessCommand(string input, short authority, Player sender)
        {
            //if (authority == 0)
             //   return false;
            if (sender != null && authority > 0)
                sender.admin = GetAdmin(sender.IP);
            string[] args = input.Split(' '.ToString().ToCharArray(),2);
            if (args[0].StartsWith("/") && args[0].Length > 2)
                args[0] = args[0].Substring(1);
            switch (args[0].ToLower())
            {
                case "help":
                    {
                        if (sender == null)
                        {
                            ConsoleWrite("SERVER CONSOLE COMMANDS:");
                            ConsoleWrite(" fps");
                            ConsoleWrite(" physics");
                            ConsoleWrite(" announce");
                            ConsoleWrite(" players");
                            ConsoleWrite(" kick <ip>");
                            ConsoleWrite(" kickn <name>");
                            ConsoleWrite(" ban <ip>");
                            ConsoleWrite(" bann <name>");
                            ConsoleWrite(" say <message>");
                            ConsoleWrite(" save <mapfile>");
                            ConsoleWrite(" load <mapfile>");
                            ConsoleWrite(" toggle <var>");
                            ConsoleWrite(" <var> <value>");
                            ConsoleWrite(" <var>");
                            ConsoleWrite(" listvars");
                            ConsoleWrite(" status");
                            ConsoleWrite(" restart");
                            ConsoleWrite(" quit");
                        }
                        else
                        {
                            SendServerMessageToPlayer(sender.Handle + ", the " + args[0].ToLower() + " command is only for use in the server console.", sender.NetConn);
                        }
                    }
                    break;
                case "players":
                    {
                        if (sender == null)
                        {
                            ConsoleWrite("( " + playerList.Count + " / " + varGetI("maxplayers") + " )");
                            foreach (Player p in playerList.Values)
                            {
                                string teamIdent = "";
                                if (p.Team == PlayerTeam.Red)
                                    teamIdent = " (R)";
                                else if (p.Team == PlayerTeam.Blue)
                                    teamIdent = " (B)";
                                if (p.IsAdmin)
                                    teamIdent += " (Admin)";
                                ConsoleWrite(p.Handle + teamIdent);
                                ConsoleWrite("  - " + p.IP);
                            }
                        }else{
                            SendServerMessageToPlayer(sender.Handle + ", the " + args[0].ToLower() + " command is only for use in the server console.", sender.NetConn);
                        }
                    }
                    break;
                case "rename":
                    {
                        if (sender != null)
                        if (args.Length == 2 && sender.Alive)
                        {
                            if (args[1].Length < 11)
                            {
                                int px = (int)sender.Position.X;
                                int py = (int)sender.Position.Y;
                                int pz = (int)sender.Position.Z;

                                for (int x = -1+px; x < 2+px; x++)
                                    for (int y = -1+py; y < 2+py; y++)
                                        for (int z = -1+pz; z < 2+pz; z++)
                                        {
                                            if (x < 1 || y < 1 || z < 1 || x > MAPSIZE - 2 || y > MAPSIZE - 2 || z > MAPSIZE - 2)
                                            {
                                                //out of map
                                            }
                                            else if (blockList[x, y, z] == BlockType.BeaconRed && sender.Team == PlayerTeam.Red)
                                            {
                                                SendServerMessageToPlayer("You renamed " + beaconList[new Vector3(x, y, z)].ID + " to " + args[1].ToUpper() + ".", sender.NetConn);
                                                if (beaconList.ContainsKey(new Vector3(x, y, z)))
                                                    beaconList.Remove(new Vector3(x, y, z));
                                                SendSetBeacon(new Vector3(x, y + 1, z), "", PlayerTeam.None);

                                                Beacon newBeacon = new Beacon();
                                                newBeacon.ID = args[1].ToUpper();
                                                newBeacon.Team = PlayerTeam.Red;
                                                beaconList[new Vector3(x, y, z)] = newBeacon;
                                                SendSetBeacon(new Vector3(x, y + 1, z), newBeacon.ID, newBeacon.Team);

                                                return true;
                                            }
                                            else if (blockList[x, y, z] == BlockType.BeaconBlue && sender.Team == PlayerTeam.Blue)
                                            {
                                                SendServerMessageToPlayer("You renamed " + beaconList[new Vector3(x, y, z)].ID + " to " + args[1].ToUpper() + ".", sender.NetConn);
                                                if (beaconList.ContainsKey(new Vector3(x, y, z)))
                                                    beaconList.Remove(new Vector3(x, y, z));
                                                SendSetBeacon(new Vector3(x, y + 1, z), "", PlayerTeam.None);

                                                Beacon newBeacon = new Beacon();
                                                newBeacon.ID = args[1].ToUpper();
                                                newBeacon.Team = PlayerTeam.Blue;
                                                beaconList[new Vector3(x, y, z)] = newBeacon;
                                                SendSetBeacon(new Vector3(x, y + 1, z), newBeacon.ID, newBeacon.Team);

                                                return true;
                                            }
                                        }

                                SendServerMessageToPlayer("You must be closer to the beacon.", sender.NetConn);
                               
                            }
                            else
                            {
                                SendServerMessageToPlayer("Beacons are restricted to 10 characters.", sender.NetConn);
                            }
                        }
                    }
                    break;
                case "fps":
                    {
                        ConsoleWrite("Server FPS:"+frameCount );
                    }
                    break;
                case "physics":
                    {
                        if (authority > 0)
                        {
                            physicsEnabled = !physicsEnabled;
                            ConsoleWrite("Physics state is now: " + physicsEnabled);
                        }
                    }
                    break;
                case "liquid":
                    {
                        if (authority > 0)
                        {
                            lavaBlockCount = 0;
                            waterBlockCount = 0;
                            int tempBlockCount = 0;

                            for (ushort i = 0; i < MAPSIZE; i++)
                                for (ushort j = 0; j < MAPSIZE; j++)
                                    for (ushort k = 0; k < MAPSIZE; k++)
                                    {
                                        if (blockList[i, j, k] == BlockType.Lava)
                                        {
                                            lavaBlockCount += 1;
                                            if (blockListContent[i, j, k, 1] > 0)
                                            {
                                                tempBlockCount += 1;
                                            }
                                        }
                                        else if (blockList[i, j, k] == BlockType.Water)
                                        {
                                            waterBlockCount += 1;
                                        }
                                    }

                            ConsoleWrite(waterBlockCount + " water blocks, " + lavaBlockCount + " lava blocks.");
                            ConsoleWrite(tempBlockCount + " temporary blocks.");
                        }
                    }
                    break;
                case "flowsleep":
                    {
                        if (authority > 0)
                        {
                            uint sleepcount = 0;

                            for (ushort i = 0; i < MAPSIZE; i++)
                                for (ushort j = 0; j < MAPSIZE; j++)
                                    for (ushort k = 0; k < MAPSIZE; k++)
                                        if (flowSleep[i, j, k] == true)
                                            sleepcount += 1;

                            ConsoleWrite(sleepcount + " liquids are happily sleeping.");
                        }
                    }
                    break;
                case "admins":
                    {
                        if (authority > 0)
                        {
                            ConsoleWrite("Admin list:");
                            foreach (string ip in admins.Keys)
                                ConsoleWrite(ip);
                        }
                    }
                    break;
                case "admin":
                    {
                        if (authority > 0)
                        {
                            if (args.Length == 2)
                            {
                                if (sender == null || sender.admin >= 2)
                                    AdminPlayer(args[1]);
                                else
                                    SendServerMessageToPlayer("You do not have the authority to add admins.", sender.NetConn);
                            }
                        }
                    }
                    break;
                case "adminn":
                    {
                        if (authority > 0)
                        {
                            if (args.Length == 2)
                            {
                                if (sender == null || sender.admin >= 2)
                                    AdminPlayer(args[1], true);
                                else
                                    SendServerMessageToPlayer("You do not have the authority to add admins.", sender.NetConn);
                            }
                        }
                    }
                    break;
                case "listvars":
                    if (sender==null)
                        varList(true);
                    else{
                        SendServerMessageToPlayer(sender.Handle + ", the " + args[0].ToLower() + " command is only for use in the server console.", sender.NetConn);
                    }
                    break;
                case "status":
                    if (sender == null)
                        status();
                    else
                        SendServerMessageToPlayer(sender.Handle + ", the " + args[0].ToLower() + " command is only for use in the server console.", sender.NetConn);
                    break;
                case "announce":
                    {
                        if (authority > 0)
                        {
                            PublicServerListUpdate(true);
                        }
                    }
                    break;
                case "kick":
                    {
                        if (authority>=1&&args.Length == 2)
                        {
                            if (sender != null)
                                ConsoleWrite("SERVER: " + sender.Handle + " has kicked " + args[1]);
                            KickPlayer(args[1]);
                        }
                    }
                    break;
                case "kickn":
                    {
                        if (authority >= 1 && args.Length == 2)
                        {
                            if (sender != null)
                                ConsoleWrite("SERVER: " + sender.Handle + " has kicked " + args[1]);
                            KickPlayer(args[1], true);
                        }
                    }
                    break;

                case "ban":
                    {
                        if (authority >= 1 && args.Length == 2)
                        {
                            if (sender != null)
                                ConsoleWrite("SERVER: " + sender.Handle + " has banned " + args[1]);
                            BanPlayer(args[1]);
                            KickPlayer(args[1]);
                        }
                    }
                    break;

                case "bann":
                    {
                        if (authority >= 1 && args.Length == 2)
                        {
                            if (sender != null)
                                ConsoleWrite("SERVER: " + sender.Handle + " has banned " + args[1]);
                            BanPlayer(args[1], true);
                            KickPlayer(args[1], true);
                        }
                    }
                    break;

                case "toggle":
                    if (authority >= 1 && args.Length == 2)
                    {
                        int exists = varExists(args[1]);
                        if (exists == 1)
                        {
                            bool val = varGetB(args[1]);
                            varSet(args[1], !val);
                        }
                        else if (exists == 2)
                            ConsoleWrite("Cannot toggle a string value.");
                        else
                            varReportStatus(args[1]);
                    }
                    else
                        ConsoleWrite("Need variable name to toggle!");
                    break;
                case "quit":
                    {
                        if (authority >= 2){
                            if ( sender!=null)
                                ConsoleWrite(sender.Handle + " is shutting down the server.");
                             keepRunning = false;
                        }
                    }
                    break;

                case "restart":
                    {
                        if (authority >= 2){
                            if (sender != null)
                                ConsoleWrite(sender.Handle + " is restarting the server.");
                            else
                            {
                                ConsoleWrite("Restarting server in 5 seconds.");
                            }
                            //disconnectAll();
                           
                            SendServerMessage("Server restarting in 5 seconds.");
                            restartTriggered = true;
                            restartTime = DateTime.Now+TimeSpan.FromSeconds(5);
                        }
                    }
                    break;

                case "say":
                    {
                        if (authority > 0)
                        {
                            if (args.Length == 2)
                            {
                                string message = "SERVER: " + args[1];
                                SendServerMessage(message);
                            }
                        }
                    }
                    break;

                case "save":
                    {
                        if (authority > 0)
                        {
                            if (args.Length >= 2)
                            {
                                if (sender != null)
                                    ConsoleWrite(sender.Handle + " is saving the map.");
                                SaveLevel(args[1]);
                            }
                        }
                    }
                    break;

                case "load":
                    {
                        if (authority > 0)
                        {
                            if (args.Length >= 2)
                            {
                                if (sender != null)
                                    ConsoleWrite(sender.Handle + " is loading a map.");
                                physicsEnabled = false;
                                Thread.Sleep(2);
                                LoadLevel(args[1]);
                                physicsEnabled = true;
                                /*if (LoadLevel(args[1]))
                                    Console.WriteLine("Loaded level " + args[1]);
                                else
                                    Console.WriteLine("Level file not found!");*/
                            }
                            else if (levelToLoad != "")
                            {
                                physicsEnabled = false;
                                Thread.Sleep(2);
                                LoadLevel(levelToLoad);
                                physicsEnabled = true;
                            }
                        }
                    }
                    break;
                default: //Check / set var
                    {
                        if (authority == 0)
                            return false;

                        string name = args[0];
                        int exists = varExists(name);
                        if (exists > 0)
                        {
                            if (args.Length == 2)
                            {
                                try
                                {
                                    if (exists == 1)
                                    {
                                        bool newVal = false;
                                        newVal = bool.Parse(args[1]);
                                        varSet(name, newVal);
                                    }
                                    else if (exists == 2)
                                    {
                                        varSet(name, args[1]);
                                    }
                                    else if (exists == 3)
                                    {
                                        varSet(name, Int32.Parse(args[1]));
                                    }

                                }
                                catch { }
                            }
                            else
                            {
                                if (sender==null)
                                    varReportStatus(name);
                                else
                                    SendServerMessageToPlayer(sender.Handle + ": The " + args[0].ToLower() + " command is only for use in the server console.",sender.NetConn);
                            }
                        }
                        else
                        {
                            char first = args[0].ToCharArray()[0];
                            if (first == 'y' || first == 'Y')
                            {
                                string message = "SERVER: " + args[0].Substring(1);
                                if (args.Length > 1)
                                    message += (message != "SERVER: " ? " " : "") + args[1];
                                SendServerMessage(message);
                            }
                            else
                            {
                                if (sender == null)
                                    ConsoleWrite("Unknown command/var.");
                                return false;
                            }
                        }
                    }
                    break;
            }
            return true;
        }

        public void MessageAll(string text)
        {
            if (announceChanges)
                SendServerMessage(text);
            ConsoleWrite(text);
        }

        public void ConsoleWrite(string text)
        {
            consoleText.Add(text);
            if (consoleText.Count > CONSOLE_SIZE)
                consoleText.RemoveAt(0);
            ConsoleRedraw();
        }

        public Dictionary<string, short> LoadAdminList()
        {
            Dictionary<string, short> temp = new Dictionary<string, short>();

            try
            {
                if (!File.Exists("admins.txt"))
                {
                    FileStream fs = File.Create("admins.txt");
                    StreamWriter sr = new StreamWriter(fs);
                    sr.WriteLine("#A list of all admins - just add one ip per line");
                    sr.Close();
                    fs.Close();
                }
                else
                {
                    FileStream file = new FileStream("admins.txt", FileMode.Open, FileAccess.Read);
                    StreamReader sr = new StreamReader(file);
                    string line = sr.ReadLine();
                    while (line != null)
                    {
                        if (line.Trim().Length!=0&&line.Trim().ToCharArray()[0]!='#')
                            temp.Add(line.Trim(), (short)2); //This will be changed to note authority too
                        line = sr.ReadLine();
                    }
                    sr.Close();
                    file.Close();
                }
            }
            catch {
                ConsoleWrite("Unable to load admin list.");
            }

            return temp;
        }

        public bool SaveAdminList()
        {
            try
            {
                FileStream file = new FileStream("admins.txt", FileMode.Create, FileAccess.Write);
                StreamWriter sw = new StreamWriter(file);
                sw.WriteLine("#A list of all admins - just add one ip per line\n");
                foreach (string ip in banList)
                    sw.WriteLine(ip);
                sw.Close();
                file.Close();
                return true;
            }
            catch { }
            return false;
        }

        public List<string> LoadBanList()
        {
            List<string> retList = new List<string>();

            try
            {
                FileStream file = new FileStream("banlist.txt", FileMode.Open, FileAccess.Read);
                StreamReader sr = new StreamReader(file);
                string line = sr.ReadLine();
                while (line != null)
                {
                    retList.Add(line.Trim());
                    line = sr.ReadLine();
                }
                sr.Close();
                file.Close();
            }
            catch { }

            return retList;
        }

        public void SaveBanList(List<string> banList)
        {
            try
            {
                FileStream file = new FileStream("banlist.txt", FileMode.Create, FileAccess.Write);
                StreamWriter sw = new StreamWriter(file);
                foreach (string ip in banList)
                    sw.WriteLine(ip);
                sw.Close();
                file.Close();
            }
            catch { }
        }

        public void KickPlayer(string ip)
        {
            KickPlayer(ip, false);
        }

        public void KickPlayer(string ip, bool name)
        {
            List<Player> playersToKick = new List<Player>();
            foreach (Player p in playerList.Values)
            {
                if ((p.IP == ip && !name) || (p.Handle.ToLower().Contains(ip.ToLower()) && name))
                    playersToKick.Add(p);
            }
            foreach (Player p in playersToKick)
            {
                p.NetConn.Disconnect("", 0);
                p.Kicked = true;
            }
        }

        public void BanPlayer(string ip)
        {
            BanPlayer(ip, false);
        }

        public void BanPlayer(string ip, bool name)
        {
            string realIp = ip;
            if (name)
            {
                foreach (Player p in playerList.Values)
                {
                    if ((p.Handle == ip && !name) || (p.Handle.ToLower().Contains(ip.ToLower()) && name))
                    {
                        realIp = p.IP;
                        break;
                    }
                }
            }
            if (!banList.Contains(realIp))
            {
                banList.Add(realIp);
                SaveBanList(banList);
            }
        }

        public short GetAdmin(string ip)
        {
            if (admins.ContainsKey(ip.Trim()))
                return admins[ip.Trim()];
            return (short)0;
        }

        public void AdminPlayer(string ip)
        {
            AdminPlayer(ip, false,(short)2);
        }

        public void AdminPlayer(string ip, bool name)
        {
            AdminPlayer(ip, name, (short)2);
        }

        public void AdminPlayer(string ip, bool name, short authority)
        {
            string realIp = ip;
            if (name)
            {
                foreach (Player p in playerList.Values)
                {
                    if ((p.Handle == ip && !name) || (p.Handle.ToLower().Contains(ip.ToLower()) && name))
                    {
                        realIp = p.IP;
                        break;
                    }
                }
            }
            if (!admins.ContainsKey(realIp))
            {
                admins.Add(realIp,authority);
                SaveAdminList();
            }
        }

        public void ConsoleProcessInput()
        {
            ConsoleWrite("> " + consoleInput);
            
            ProcessCommand(consoleInput, (short)2, null);
            /*string[] args = consoleInput.Split(" ".ToCharArray(),2);

            
            switch (args[0].ToLower().Trim())
            {
                case "help":
                    {
                        ConsoleWrite("SERVER CONSOLE COMMANDS:");
                        ConsoleWrite(" announce");
                        ConsoleWrite(" players");
                        ConsoleWrite(" kick <ip>");
                        ConsoleWrite(" kickn <name>");
                        ConsoleWrite(" ban <ip>");
                        ConsoleWrite(" bann <name>");
                        ConsoleWrite(" say <message>");
                        ConsoleWrite(" save <mapfile>");
                        ConsoleWrite(" load <mapfile>");
                        ConsoleWrite(" toggle <var>");//ConsoleWrite(" toggle [" + varList() + "]");//[tnt,stnt,sspreads,insanelava,minelava,announcechanges]");
                        ConsoleWrite(" <var> <value>");
                        ConsoleWrite(" <var>");
                        ConsoleWrite(" listvars");
                        ConsoleWrite(" status");
                        ConsoleWrite(" restart");
                        //ConsoleWrite(" reload");
                        ConsoleWrite(" quit");
                    }
                    break;
                case "players":
                    {
                        ConsoleWrite("( " + playerList.Count + " / " + varGetI("maxplayers") + " )");//maxPlayers + " )");
                        foreach (Player p in playerList.Values)
                        {
                            string teamIdent = "";
                            if (p.Team == PlayerTeam.Red)
                                teamIdent = " (R)";
                            else if (p.Team == PlayerTeam.Blue)
                                teamIdent = " (B)";
                            ConsoleWrite(p.Handle + teamIdent);
                            ConsoleWrite("  - " + p.IP);
                        }
                    }
                    break;
                case "listvars":
                    varList(true);
                    break;
                case "announce":
                    {
                        PublicServerListUpdate(true);
                    }
                    break;
                case "kick":
                    {
                        if (args.Length == 2)
                        {
                            KickPlayer(args[1]);
                        }
                    }
                    break;
                case "kickn":
                    {
                        if (args.Length == 2)
                        {
                            KickPlayer(args[1], true);
                        }
                    }
                    break;

                case "ban":
                    {
                        if (args.Length == 2)
                        {
                            KickPlayer(args[1]);
                            BanPlayer(args[1]);
                        }
                    }
                    break;

                case "bann":
                    {
                        if (args.Length == 2)
                        {
                            KickPlayer(args[1],true);
                            BanPlayer(args[1],true);
                        }
                    }
                    break;

                case "toggle":
                    if (args.Length == 2)
                    {
                        int exists = varExists(args[1]);
                        if (exists == 1)
                        {
                            bool val = varGetB(args[1]);
                            varSet(args[1], !val);
                        }
                        else if (exists == 2)
                            ConsoleWrite("Cannot toggle a string value.");
                        else
                            varReportStatus(args[1]);
                    }
                    else
                        ConsoleWrite("Need variable name to toggle!");
                    break;
                case "quit":
                    {
                        keepRunning = false;
                    }
                    break;

                case "restart":
                    {
                        disconnectAll();
                        restartTriggered = true;
                        restartTime = DateTime.Now;
                    }
                    break;

                case "say":
                    {
                        if (args.Length == 2)
                        {
                            string message = "SERVER: " + args[1];
                            SendServerMessage(message);
                        }
                    }
                    break;

                case "save":
                    {
                        if (args.Length >= 2)
                        {
                            SaveLevel(args[1]);
                        }
                    }
                    break;

                case "load":
                    {
                        if (args.Length >= 2)
                        {
                            LoadLevel(args[1]);
                        }
                        else if (levelToLoad != "")
                        {
                            LoadLevel(levelToLoad);
                        }
                    }
                    break;
                case "status":
                    status();
                    break;
                default: //Check / set var
                    {
                        string name = args[0];
                        int exists = varExists(name);
                        if (exists > 0)
                        {
                            if (args.Length == 2)
                            {
                                try
                                {
                                    if (exists == 1)
                                    {
                                        bool newVal = false;
                                        newVal = bool.Parse(args[1]);
                                        varSet(name, newVal);
                                    }
                                    else if (exists == 2)
                                    {
                                        varSet(name, args[1]);
                                    }
                                    else if (exists == 3)
                                    {
                                        varSet(name, Int32.Parse(args[1]));
                                    }

                                }
                                catch { }
                            }
                            else
                            {
                                varReportStatus(name);
                            }
                        }
                        else
                        {
                            char first=args[0].ToCharArray()[0];
                            if (first == 'y' || first == 'Y')
                            {
                                string message = "SERVER: " + args[0].Substring(1);
                                if (args.Length > 1)
                                    message += (message!="SERVER: " ? " " : "") + args[1];
                                SendServerMessage(message);
                            }
                            else
                                ConsoleWrite("Unknown command/var.");
                        }
                    }
                    break;
            }*/

            consoleInput = "";
            ConsoleRedraw();
        }

        public void SaveLevel(string filename)
        {
            FileStream fs = new FileStream(filename, FileMode.Create);
            StreamWriter sw = new StreamWriter(fs);
            for (int x = 0; x < MAPSIZE; x++)
                for (int y = 0; y < MAPSIZE; y++)
                    for (int z = 0; z < MAPSIZE; z++)
                        sw.WriteLine((byte)blockList[x, y, z] + "," + (byte)blockCreatorTeam[x, y, z]);
            sw.Close();
            fs.Close();
        }

        public bool LoadLevel(string filename)
        {
            try
            {
                if (!File.Exists(filename))
                {
                    ConsoleWrite("Unable to load level - " + filename + " does not exist!");
                    return false;
                }
                SendServerMessage("Changing map to " + filename + "!");
                disconnectAll();
                
                FileStream fs = new FileStream(filename, FileMode.Open);
                StreamReader sr = new StreamReader(fs);
                for (int x = 0; x < MAPSIZE; x++)
                    for (int y = 0; y < MAPSIZE; y++)
                        for (int z = 0; z < MAPSIZE; z++)
                        {
                            string line = sr.ReadLine();
                            string[] fileArgs = line.Split(",".ToCharArray());
                            if (fileArgs.Length == 2)
                            {
                                blockList[x, y, z] = (BlockType)int.Parse(fileArgs[0], System.Globalization.CultureInfo.InvariantCulture);
                                blockCreatorTeam[x, y, z] = (PlayerTeam)int.Parse(fileArgs[1], System.Globalization.CultureInfo.InvariantCulture);
                            }
                        }
                sr.Close();
                fs.Close();
                for (ushort i = 0; i < MAPSIZE; i++)
                    for (ushort j = 0; j < MAPSIZE; j++)
                        for (ushort k = 0; k < MAPSIZE; k++)
                            flowSleep[i, j, k] = false;
                ConsoleWrite("Level loaded successfully - now playing " + filename + "!");
                return true;
            }
            catch { }
            return false;
        }

        public void ResetLevel()
        {
            disconnectAll();
            newMap();
        }

        public void disconnectAll()
        {
            foreach (Player p in playerList.Values)
            {
                p.NetConn.Disconnect("",0);  
            }
            playerList.Clear();
        }

        public void ConsoleRedraw()
        {
            Console.Clear();
            ConsoleDrawCentered("INFINIMINER SERVER " + Defines.INFINIMINER_VERSION, 0);
            ConsoleDraw("================================================================================", 0, 1);
            for (int i = 0; i < consoleText.Count; i++)
                ConsoleDraw(consoleText[i], 0, i + 2);
            ConsoleDraw("================================================================================", 0, CONSOLE_SIZE + 2);
            ConsoleDraw("> " + consoleInput, 0, CONSOLE_SIZE + 3);
        }

        public void ConsoleDraw(string text, int x, int y)
        {
            Console.SetCursorPosition(x, y);
            Console.Write(text);
        }

        public void ConsoleDrawCentered(string text, int y)
        {
            Console.SetCursorPosition(40 - text.Length / 2, y);
            Console.Write(text);
        }

        List<string> beaconIDList = new List<string>();
        Dictionary<Vector3, Beacon> beaconList = new Dictionary<Vector3, Beacon>();
        List<uint> itemIDList = new List<uint>();
        Dictionary<uint, Item> itemList = new Dictionary<uint, Item>();
        int highestitem = 0;

        Random randGen = new Random();
        int frameid = 10000;
        public string _GenerateBeaconID()
        {
            string id = "K";
            for (int i = 0; i < 3; i++)
                id += (char)randGen.Next(48, 58);
            return id;
        }
        public string GenerateBeaconID()
        {
            string newId = _GenerateBeaconID();
            while (beaconIDList.Contains(newId))
                newId = _GenerateBeaconID();
            beaconIDList.Add(newId);
            return newId;
        }

        public uint _GenerateItemID()
        {
            uint id = (uint)(randGen.Next(1, 2300000));
            return id;
        }
        public uint GenerateItemID()
        {
            uint newId = 1;// _GenerateItemID();
            while (itemIDList.Contains(newId))
            {
                newId++;// = _GenerateItemID();
            }

            if (newId > highestitem)
                highestitem = (int)newId;

            itemIDList.Add(newId);
            return newId;
        }
        public uint SetItem(ItemType iType, Vector3 pos, Vector3 heading, Vector3 vel, PlayerTeam team, int val)
        {
            if(iType == ItemType.Gold || iType == ItemType.Ore)//merge minerals on the ground
            foreach (KeyValuePair<uint, Item> iF in itemList)//pretty inefficient
            {
                    if (Distf(pos, iF.Value.Position) < 2.0f)
                    {
                        if (iType == iF.Value.Type && !iF.Value.Disposing && iF.Value.Content[5] < 10)//limit stacks to 10
                        {
                            iF.Value.Content[5] += 1;//supposed ore content
                            iF.Value.Scale = 0.5f + (float)(iF.Value.Content[5]) * 0.1f;
                            SendItemScaleUpdate(iF.Value);
                            return iF.Key;//item does not get created, instead merges
                        }
                    }
            }
            
                Item newItem = new Item(null, iType);
                newItem.ID = GenerateItemID();
                newItem.Team = team;
                newItem.Heading = heading;
                newItem.Position = pos;
                newItem.Velocity = vel;

                if (iType == ItemType.Artifact)
                {
                    newItem.Content[10] = val;
                    if (newItem.Content[10] == 0)//undefined artifact, give it a random color
                    {
                        newItem.Content[1] = (int)(randGen.NextDouble() * 100);//r
                        newItem.Content[2] = (int)(randGen.NextDouble() * 100);//g
                        newItem.Content[3] = (int)(randGen.NextDouble() * 100);//b
                    }
                    else if (newItem.Content[10] == 1)//material artifact: generates 10 ore periodically
                    {
                        newItem.Content[1] = (int)(0.6 * 100);//r
                        newItem.Content[2] = (int)(0.6 * 100);//g
                        newItem.Content[3] = (int)(0.6 * 100);//b
                    }
                    else if (newItem.Content[10] == 2)//vampiric artifact
                    {
                        newItem.Content[1] = (int)(0.5 * 100);//r
                        newItem.Content[2] = (int)(0.1 * 100);//g
                        newItem.Content[3] = (int)(0.1 * 100);//b
                    }
                    else if (newItem.Content[10] == 3)//regeneration artifact
                    {
                        newItem.Content[1] = (int)(0.3 * 100);//r
                        newItem.Content[2] = (int)(0.9 * 100);//g
                        newItem.Content[3] = (int)(0.3 * 100);//b
                    }
                    else if (newItem.Content[10] == 4)//aqua artifact: personal: gives waterbreathing, waterspeed and digging underwater, team: gives team water breathing and ability to dig underwater
                    {
                        newItem.Content[1] = (int)(0.5 * 100);//r
                        newItem.Content[2] = (int)(0.5 * 100);//g
                        newItem.Content[3] = (int)(0.8 * 100);//b
                    }
                    else if (newItem.Content[10] == 5)//golden artifact: personal: converts ore to gold, team: generates gold slowly
                    {
                        newItem.Content[1] = (int)(0.87 * 100);//r
                        newItem.Content[2] = (int)(0.71 * 100);//g
                        newItem.Content[3] = (int)(0.25 * 100);//b
                    }
                    else if (newItem.Content[10] == 6)//storm artifact: ground: creates water in empty spaces, personal: periodically shocks opponents, team: 
                    {
                        newItem.Content[1] = (int)(0.1 * 100);//r
                        newItem.Content[2] = (int)(0.4 * 100);//g
                        newItem.Content[3] = (int)(0.9 * 100);//b
                    }
                    else if (newItem.Content[10] == 7)//reflection artifact: ground: repels bombs, personal: reflects half damage, team: 
                    {
                        newItem.Content[1] = (int)(0.3 * 100);//r
                        newItem.Content[2] = (int)(0.3 * 100);//g
                        newItem.Content[3] = (int)(0.3 * 100);//b
                    }
                    else if (newItem.Content[10] == 8)//medical artifact: ground: heals any players nearby, personal: allows player to hit friendlies to heal them, team: 
                    {
                        newItem.Content[1] = (int)(0.1 * 100);//r
                        newItem.Content[2] = (int)(1.0 * 100);//g
                        newItem.Content[3] = (int)(0.1 * 100);//b
                    }
                    else if (newItem.Content[10] == 9)//stone artifact: ground: causes blocks to fall that arent attached to similar types, personal: immune to knockback, team: reduces knockback
                    {
                        newItem.Content[1] = (int)(0.9 * 100);//r
                        newItem.Content[2] = (int)(0.7 * 100);//g
                        newItem.Content[3] = (int)(0.7 * 100);//b
                    }
                }
                else if (iType == ItemType.Bomb)
                {
                    newItem.Content[1] = 100;//r
                    newItem.Content[2] = 100;//g
                    newItem.Content[3] = 100;//b
                    newItem.Content[5] = 80;//4 second fuse
                    newItem.Weight = 1.5f;
                }
                else if (iType == ItemType.Rope)
                {
                    newItem.Content[1] = 100;//r
                    newItem.Content[2] = 100;//g
                    newItem.Content[3] = 100;//b
                    newItem.Weight = 0.6f;
                }
                else
                {
                    newItem.Content[1] = 100;//r
                    newItem.Content[2] = 100;//g
                    newItem.Content[3] = 100;//b
                }

                itemList[newItem.ID] = newItem;
                SendSetItem(newItem.ID, newItem.Type, newItem.Position, newItem.Team, newItem.Heading);
                return newItem.ID;
        }

        public void SetBlockForPlayer(ushort x, ushort y, ushort z, BlockType blockType, PlayerTeam team, Player player)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.BlockSet);
            msgBuffer.Write((byte)x);
            msgBuffer.Write((byte)y);
            msgBuffer.Write((byte)z);

            if (blockType == BlockType.Vacuum)
            {
                msgBuffer.Write((byte)BlockType.None);
            }
            else
            {
                msgBuffer.Write((byte)blockType);
            }

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                {
                    if (playerList[netConn] == player)
                    {
                        netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
                        return;
                    }
                }
        }

        public void SetBlock(ushort x, ushort y, ushort z, BlockType blockType, PlayerTeam team)//dont forget duplicate function SetBlock
        {
            if (x <= 0 || y <= 0 || z <= 0 || (int)x > MAPSIZE - 1 || (int)y > MAPSIZE - 1 || (int)z > MAPSIZE - 1)
                return;

            if (blockType == BlockType.None)//block removed, we must unsleep liquids nearby
            {

                Disturb(x, y, z);
            }

            blockListContent[x, y, z, 0] = 0;//dangerous stuff can happen if we dont set this

            if (blockType == BlockType.BeaconRed || blockType == BlockType.BeaconBlue)
            {
                Beacon newBeacon = new Beacon();
                newBeacon.ID = GenerateBeaconID();
                newBeacon.Team = blockType == BlockType.BeaconRed ? PlayerTeam.Red : PlayerTeam.Blue;
                beaconList[new Vector3(x, y, z)] = newBeacon;
                SendSetBeacon(new Vector3(x, y+1, z), newBeacon.ID, newBeacon.Team);
            }
            else if (blockType == BlockType.ResearchR || blockType == BlockType.ResearchB)
            {
                blockListContent[x, y, z, 1] = 0;//activated
                blockListContent[x, y, z, 2] = 0;//topic
                blockListContent[x, y, z, 3] = 0;//progress points
                blockListContent[x, y, z, 4] = 0;//timer between updates
            }
            else if (blockType == BlockType.Pipe)
            {
                blockListContent[x, y, z, 1] = 0;//Is pipe connected? [0-1]
                blockListContent[x, y, z, 2] = 0;//Is pipe a source? [0-1]
                blockListContent[x, y, z, 3] = 0;//Pipes connected
                blockListContent[x, y, z, 4] = 0;//Is pipe destination?
                blockListContent[x, y, z, 5] = 0;//src x
                blockListContent[x, y, z, 6] = 0;//src y
                blockListContent[x, y, z, 7] = 0;//src z
                blockListContent[x, y, z, 8] = 0;//pipe must not contain liquid
            }
            else if (blockType == BlockType.Barrel)
            {
                blockListContent[x, y, z, 1] = 0;//containtype
                blockListContent[x, y, z, 2] = 0;//amount
                blockListContent[x, y, z, 3] = 0;
            }
            else if (blockType == BlockType.Plate)
            {
                blockListContent[x, y, z, 1] = 0;
                blockListContent[x, y, z, 2] = 6;
            }
            else if (blockType == BlockType.Hinge)
            {
                blockListContent[x, y, z, 1] = 0;//rotation state [0-1] 0: flat 1: vertical
                blockListContent[x, y, z, 2] = 2;//rotation 
                blockListContent[x, y, z, 3] = 0;//attached block count
                blockListContent[x, y, z, 4] = 0;//attached block count
                blockListContent[x, y, z, 5] = 0;//attached block count
                blockListContent[x, y, z, 6] = 0;//start of block array
            }
            else if (blockType == BlockType.Pump)
            {
                blockListContent[x, y, z, 1] = 0;//direction
                blockListContent[x, y, z, 2] = 0;//x input
                blockListContent[x, y, z, 3] = -1;//y input
                blockListContent[x, y, z, 4] = 0;//z input
                blockListContent[x, y, z, 5] = 0;//x output
                blockListContent[x, y, z, 6] = 1;//y output
                blockListContent[x, y, z, 7] = 0;//z output
            }

            if (blockType == BlockType.None && (blockList[x, y, z] == BlockType.BeaconRed || blockList[x, y, z] == BlockType.BeaconBlue))
            {
                if (beaconList.ContainsKey(new Vector3(x,y,z)))
                    beaconList.Remove(new Vector3(x,y,z));
                SendSetBeacon(new Vector3(x, y+1, z), "", PlayerTeam.None);
            }

            if (blockType == blockList[x, y, z])//duplicate block, no need to send players data
            {
                blockList[x, y, z] = blockType;
                blockCreatorTeam[x, y, z] = team;
                flowSleep[x, y, z] = false;
            }
            else
            {
             
                blockList[x, y, z] = blockType;
                blockCreatorTeam[x, y, z] = team;
                blockListHP[x, y, z] = BlockInformation.GetHP(blockType);
                flowSleep[x, y, z] = false;

                // x, y, z, type, all bytes
                NetBuffer msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.BlockSet);
                msgBuffer.Write((byte)x);
                msgBuffer.Write((byte)y);
                msgBuffer.Write((byte)z);
                if (blockType == BlockType.Vacuum)
                {
                    msgBuffer.Write((byte)BlockType.None);
                }
                else
                {
                    msgBuffer.Write((byte)blockType);
                }
                foreach (NetConnection netConn in playerList.Keys)
                    if (netConn.Status == NetConnectionStatus.Connected)
                        netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);

            }
            //ConsoleWrite("BLOCKSET: " + x + " " + y + " " + z + " " + blockType.ToString());
        }

        public void SetBlockDebris(ushort x, ushort y, ushort z, BlockType blockType, PlayerTeam team)//dont forget duplicate function SetBlock
        {
            if (x <= 0 || y <= 0 || z <= 0 || (int)x > MAPSIZE - 1 || (int)y > MAPSIZE - 1 || (int)z > MAPSIZE - 1)
                return;

            if (blockType == BlockType.None)//block removed, we must unsleep liquids nearby
            {
                Disturb(x, y, z);
            }

            blockListContent[x, y, z, 0] = 0;//dangerous stuff can happen if we dont set this

            if (blockType == BlockType.BeaconRed || blockType == BlockType.BeaconBlue)
            {
                Beacon newBeacon = new Beacon();
                newBeacon.ID = GenerateBeaconID();
                newBeacon.Team = blockType == BlockType.BeaconRed ? PlayerTeam.Red : PlayerTeam.Blue;
                beaconList[new Vector3(x, y, z)] = newBeacon;
                SendSetBeacon(new Vector3(x, y + 1, z), newBeacon.ID, newBeacon.Team);
            }
            else if (blockType == BlockType.Pipe)
            {
                blockListContent[x, y, z, 1] = 0;//Is pipe connected? [0-1]
                blockListContent[x, y, z, 2] = 0;//Is pipe a source? [0-1]
                blockListContent[x, y, z, 3] = 0;//Pipes connected
                blockListContent[x, y, z, 4] = 0;//Is pipe destination?
                blockListContent[x, y, z, 5] = 0;//src x
                blockListContent[x, y, z, 6] = 0;//src y
                blockListContent[x, y, z, 7] = 0;//src z
                blockListContent[x, y, z, 8] = 0;//pipe must not contain liquid
            }
            else if (blockType == BlockType.Barrel)
            {
                blockListContent[x, y, z, 1] = 0;//containtype
                blockListContent[x, y, z, 2] = 0;//amount
                blockListContent[x, y, z, 3] = 0;
            }
            else if (blockType == BlockType.Pump)
            {
                blockListContent[x, y, z, 1] = 0;//direction
                blockListContent[x, y, z, 2] = 0;//x input
                blockListContent[x, y, z, 3] = -1;//y input
                blockListContent[x, y, z, 4] = 0;//z input
                blockListContent[x, y, z, 5] = 0;//x output
                blockListContent[x, y, z, 6] = 1;//y output
                blockListContent[x, y, z, 7] = 0;//z output
            }

            if (blockType == BlockType.None && (blockList[x, y, z] == BlockType.BeaconRed || blockList[x, y, z] == BlockType.BeaconBlue))
            {
                if (beaconList.ContainsKey(new Vector3(x, y, z)))
                    beaconList.Remove(new Vector3(x, y, z));
                SendSetBeacon(new Vector3(x, y + 1, z), "", PlayerTeam.None);
            }

            if (blockType == blockList[x, y, z])//duplicate block, no need to send players data
            {
                blockList[x, y, z] = blockType;
                blockCreatorTeam[x, y, z] = team;
                flowSleep[x, y, z] = false;
            }
            else
            {
                
                blockList[x, y, z] = blockType;
                blockCreatorTeam[x, y, z] = team;
                flowSleep[x, y, z] = false;

                // x, y, z, type, all bytes
                NetBuffer msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.BlockSetDebris);
                msgBuffer.Write((byte)x);
                msgBuffer.Write((byte)y);
                msgBuffer.Write((byte)z);
                if (blockType == BlockType.Vacuum)
                {
                    msgBuffer.Write((byte)BlockType.None);
                }
                else
                {
                    msgBuffer.Write((byte)blockType);
                }
                foreach (NetConnection netConn in playerList.Keys)
                    if (netConn.Status == NetConnectionStatus.Connected)
                        netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);

            }
            //ConsoleWrite("BLOCKSET: " + x + " " + y + " " + z + " " + blockType.ToString());
        }

        public void createBase(PlayerTeam team)
        {
            int pos = randGen.Next(10, 50);
            int posy = 61 - randGen.Next(10, 20);

            if(team == PlayerTeam.Red)
            {
                for (int a = -10; a < 10; a++)
                    for (int b = -3; b < 3; b++)
                        for (int c = -10; c < 10; c++)//clear rock
                        {
                            if (blockList[pos + a, posy + b, 50 + c] == BlockType.Rock)
                            {
                                blockList[pos + a, posy + b, 50 + c] = BlockType.Dirt;
                            }
                        }

                for (int a = -3; a < 3; a++)
                    for (int b = -2; b < 3; b++)
                        for (int c = -3; c < 3; c++)//place outer shell
                        {
                            blockList[pos + a, posy + b, 50 + c] = BlockType.SolidRed2;
                            blockListHP[pos + a, posy + b, 50 + c] = 400;
                            blockCreatorTeam[pos + a, posy + b, 50 + c] = PlayerTeam.None;
                        }

                for (int a = -2; a < 2; a++)
                    for (int b = -1; b < 2; b++)
                        for (int c = -2; c < 2; c++)//prevent players from adding stuff to it
                        {
                            blockList[pos + a, posy + b, 50 + c] = BlockType.Vacuum;
                        }

                blockList[pos, posy - 1, 50 - 3] = BlockType.TransRed;
                blockList[pos, posy, 50 - 3] = BlockType.TransRed;
                blockList[pos-1, posy - 1, 50 - 3] = BlockType.TransRed;
                blockList[pos-1, posy, 50 - 3] = BlockType.TransRed;

                blockList[pos, posy - 1, 50 - 4] = BlockType.None;
                blockList[pos, posy, 50 - 4] = BlockType.None;
                blockList[pos - 1, posy - 1, 50 - 4] = BlockType.None;
                blockList[pos - 1, posy, 50 - 4] = BlockType.None;

                RedBase = new PlayerBase();
                basePosition.Add(PlayerTeam.Red,RedBase);
                basePosition[PlayerTeam.Red].team = PlayerTeam.Red;
                basePosition[PlayerTeam.Red].X = pos;
                basePosition[PlayerTeam.Red].Y = posy;
                basePosition[PlayerTeam.Red].Z = 50;
                blockList[pos - 2, posy - 1, 51] = BlockType.BaseRed;
                //SetBlock((ushort)(pos - 2), (ushort)(posy - 1), 50, BlockType.BeaconRed, PlayerTeam.Red);
                blockList[pos - 2, posy - 1, 49] = BlockType.BankRed;

                Beacon newBeacon = new Beacon();
                newBeacon.ID = "HOME";
                newBeacon.Team = PlayerTeam.Red;
                beaconList[new Vector3(pos - 2, posy - 1, 50)] = newBeacon;
                SendSetBeacon(new Vector3(pos - 2, posy, 50), newBeacon.ID, newBeacon.Team);
            }
            else
            {
                for (int a = -10; a < 10; a++)
                    for (int b = -3; b < 3; b++)
                        for (int c = -10; c < 10; c++)
                        {
                            if (blockList[pos + a, posy + b, 14 + c] == BlockType.Rock)
                            {
                                blockList[pos + a, posy + b, 14 + c] = BlockType.Dirt;
                            }
                        }

                for (int a = -3; a < 3; a++)
                    for (int b = -3; b < 3; b++)
                        for (int c = -3; c < 3; c++)
                        {
                            blockList[pos + a, posy + b, 14 + c] = BlockType.SolidBlue2;
                            blockListHP[pos + a, posy + b, 14 + c] = 400;
                            blockCreatorTeam[pos + a, posy + b, 14 + c] = PlayerTeam.None;
                        }

                for (int a = -2; a < 2; a++)
                    for (int b = -1; b < 2; b++)
                        for (int c = -2; c < 2; c++)
                        {
                            blockList[pos + a, posy + b, 14 + c] = BlockType.Vacuum;
                        }

                blockList[pos, posy - 1, 14 + 2] = BlockType.TransBlue;
                blockList[pos, posy, 14 + 2] = BlockType.TransBlue;
                blockList[pos - 1, posy - 1, 14 + 2] = BlockType.TransBlue;
                blockList[pos - 1, posy, 14 + 2] = BlockType.TransBlue;

                blockList[pos, posy - 1, 14 + 3] = BlockType.None;
                blockList[pos, posy, 14 + 3] = BlockType.None;
                blockList[pos - 1, posy - 1, 14 + 3] = BlockType.None;
                blockList[pos - 1, posy, 14 + 3] = BlockType.None;

                BlueBase = new PlayerBase();
                basePosition.Add(PlayerTeam.Blue,BlueBase);
                basePosition[PlayerTeam.Blue].team = PlayerTeam.Blue;
                basePosition[PlayerTeam.Blue].X = pos;
                basePosition[PlayerTeam.Blue].Y = posy;
                basePosition[PlayerTeam.Blue].Z = 14;
                blockList[pos-2, posy-1, 13] = BlockType.BaseBlue;
                blockList[pos-2, posy-1, 15] = BlockType.BankBlue;
                //SetBlock((ushort)(pos - 2), (ushort)(posy - 1), 14, BlockType.BeaconBlue, PlayerTeam.Blue);
                Beacon newBeacon = new Beacon();
                newBeacon.ID = "HOME";
                newBeacon.Team = PlayerTeam.Blue;
                beaconList[new Vector3(pos - 2, posy - 1, 14)] = newBeacon;
                SendSetBeacon(new Vector3(pos - 2, posy, 14), newBeacon.ID, newBeacon.Team);
            }
        }
        public int newMap()
        {
            physicsEnabled = false;
            Thread.Sleep(2);

            // Create our block world, translating the coordinates out of the cave generator (where Z points down)
            BlockType[, ,] worldData = CaveGenerator.GenerateCaveSystem(MAPSIZE, includeLava, oreFactor, includeWater);
            blockList = new BlockType[MAPSIZE, MAPSIZE, MAPSIZE];
            blockListContent = new Int32[MAPSIZE, MAPSIZE, MAPSIZE, 50];
            blockListHP = new Int32[MAPSIZE, MAPSIZE, MAPSIZE];
            blockCreatorTeam = new PlayerTeam[MAPSIZE, MAPSIZE, MAPSIZE];

            ResearchComplete = new Int32[3, 20];
            ResearchProgress = new Int32[3, 20];
            artifactActive = new Int32[3, 20];
            allowBlock = new bool[3, 6, (byte)BlockType.MAXIMUM];

            for (int cr = 0; cr < 20; cr++)//clear artifact stores
            {
                artifactActive[0, cr] = 0;
                artifactActive[1, cr] = 0;
                artifactActive[2, cr] = 0;
            }

            for (ushort ct = 0; ct < 3; ct++)
            {
                for (ushort ca = 0; ca < 6; ca++)
                {
                    for (ushort cb = 0; cb < (byte)BlockType.MAXIMUM; cb++)
                    {
                        allowBlock[ct, ca, cb] = false;
                    }
                }
            }

            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.SolidRed] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.ArtCaseR] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.BankRed] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.BeaconRed] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.Barrel] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.GlassR] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.Hinge] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.Jump] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.Ladder] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.Lever] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.Metal] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.Pipe] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.Pump] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.RadarRed] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.ResearchR] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.Shock] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.StealthBlockR] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.TrapR] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Engineer, (byte)BlockType.Plate] = true;

            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.SolidBlue] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.ArtCaseB] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.BankBlue] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.BeaconBlue] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.Barrel] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.GlassB] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.Hinge] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.Jump] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.Ladder] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.Lever] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.Metal] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.Pipe] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.Pump] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.RadarBlue] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.ResearchB] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.Shock] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.StealthBlockB] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.TrapR] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Engineer, (byte)BlockType.Plate] = true;

            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Miner, (byte)BlockType.SolidRed] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Miner, (byte)BlockType.BeaconRed] = true; 
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Miner, (byte)BlockType.Hinge] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Miner, (byte)BlockType.Lever] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Miner, (byte)BlockType.Shock] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Miner, (byte)BlockType.Plate] = true;

            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Miner, (byte)BlockType.SolidBlue] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Miner, (byte)BlockType.BeaconBlue] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Miner, (byte)BlockType.Hinge] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Miner, (byte)BlockType.Lever] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Miner, (byte)BlockType.Shock] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Miner, (byte)BlockType.Plate] = true;

            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Prospector, (byte)BlockType.SolidRed] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Prospector, (byte)BlockType.BeaconRed] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Prospector, (byte)BlockType.Hinge] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Prospector, (byte)BlockType.Lever] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Prospector, (byte)BlockType.Shock] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Prospector, (byte)BlockType.Plate] = true;

            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Prospector, (byte)BlockType.SolidBlue] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Prospector, (byte)BlockType.BeaconBlue] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Prospector, (byte)BlockType.Hinge] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Prospector, (byte)BlockType.Lever] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Prospector, (byte)BlockType.Shock] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Prospector, (byte)BlockType.Plate] = true;

            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Sapper, (byte)BlockType.SolidRed] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Sapper, (byte)BlockType.BeaconRed] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Sapper, (byte)BlockType.Hinge] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Sapper, (byte)BlockType.Lever] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Sapper, (byte)BlockType.Shock] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Sapper, (byte)BlockType.Explosive] = true;
            allowBlock[(byte)PlayerTeam.Red, (byte)PlayerClass.Sapper, (byte)BlockType.Plate] = true;

            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Sapper, (byte)BlockType.SolidBlue] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Sapper, (byte)BlockType.BeaconBlue] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Sapper, (byte)BlockType.Hinge] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Sapper, (byte)BlockType.Lever] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Sapper, (byte)BlockType.Shock] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Sapper, (byte)BlockType.Explosive] = true;
            allowBlock[(byte)PlayerTeam.Blue, (byte)PlayerClass.Sapper, (byte)BlockType.Plate] = true;

            for (ushort c = 0; c < 10; c++)
            {
                ResearchComplete[1, c] = 0;
                ResearchProgress[1, c] = ResearchInformation.GetCost((Research)c);
                ResearchComplete[2, c] = 0;
                ResearchProgress[2, c] = ResearchInformation.GetCost((Research)c);
            }

            for (ushort i = 0; i < MAPSIZE; i++)
                for (ushort j = 0; j < MAPSIZE; j++)
                    for (ushort k = 0; k < MAPSIZE; k++)
                    {
                        flowSleep[i, j, k] = false;
                        blockList[i, (ushort)(MAPSIZE - 1 - k), j] = worldData[i, j, k];
                        //if (blockList[i, (ushort)(MAPSIZE - 1 - k), j] == BlockType.Dirt)
                        //{
                        //    blockList[i, (ushort)(MAPSIZE - 1 - k), j] = BlockType.Sand;//covers map with block
                        //}
                        for (ushort c = 0; c < 20; c++)
                        {
                            blockListContent[i, (ushort)(MAPSIZE - 1 - k), k, c] = 0;//content data for blocks, such as pumps
                        }

                        blockListHP[i, (ushort)(MAPSIZE - 1 - k), j] = BlockInformation.GetHP(blockList[i, (ushort)(MAPSIZE - 1 - k), j]);

                        if (blockList[i, (ushort)(MAPSIZE - 1 - k), j] == BlockType.Gold)
                        {
                            blockListHP[i, (ushort)(MAPSIZE - 1 - k), j] = 40;
                        }
                        else if (blockList[i, (ushort)(MAPSIZE - 1 - k), j] == BlockType.Diamond)
                        {
                            blockListHP[i, (ushort)(MAPSIZE - 1 - k), j] = 120;
                        }
                        else
                        {
                           
                        }

                        blockCreatorTeam[i, j, k] = PlayerTeam.None;

                        if (i < 1 || j < 1 || k < 1 || i > MAPSIZE - 2 || j > MAPSIZE - 2 || k > MAPSIZE - 2)
                        {
                            blockList[i, (ushort)(MAPSIZE - 1 - k), j] = BlockType.None;
                        }                        
                    }

            for (ushort i = 0; i < MAPSIZE; i++)
                for (ushort j = 0; j < MAPSIZE; j++)
                    for (ushort k = 0; k < MAPSIZE; k++)
                    {
                        if (blockList[i, (ushort)(MAPSIZE - 1 - k), j] != BlockType.MagmaVent && blockList[i, (ushort)(MAPSIZE - 1 - k), j] != BlockType.Spring)
                            if (randGen.Next(500) == 1)
                            {
                                if (i < 1 || j < 1 || k < 1 || i > MAPSIZE - 2 || j > MAPSIZE - 2 || k > MAPSIZE - 2)
                                {
                                }
                                else
                                {
                                    if (blockList[i - 1, (ushort)(MAPSIZE - 1 - k), j] != BlockType.None)
                                        if (blockList[i + 1, (ushort)(MAPSIZE - 1 - k), j] != BlockType.None)
                                            if (blockList[i, (ushort)(MAPSIZE - k), j] != BlockType.None)
                                                if (blockList[i, (ushort)(MAPSIZE + 1 - k), j] != BlockType.None)
                                                    if (blockList[i, (ushort)(MAPSIZE - 1 - k), j - 1] != BlockType.None)
                                                        if (blockList[i, (ushort)(MAPSIZE - 1 - k), j + 1] != BlockType.None)
                                                            blockList[i, (ushort)(MAPSIZE - 1 - k), j] = BlockType.MagmaBurst;
                                }
                            }
                    }
            //add bases
            createBase(PlayerTeam.Red);
            createBase(PlayerTeam.Blue);

            for (ushort i = 0; i < MAPSIZE; i++)
                for (ushort k = 0; k < MAPSIZE; k++)
                    for (ushort j = (ushort)(MAPSIZE-1); j > 0; j--)
                    {
                        if (blockList[i, j, k] == BlockType.Dirt || blockList[i, j, k] == BlockType.Grass)
                        {
                            blockList[i, j, k] = BlockType.Grass;
                            blockListContent[i, j, k, 0] = 300;//greenery may reside here
                            break;
                        }
                        else if (blockList[i, j, k] != BlockType.None)
                        {
                            break;
                        }
                    }
                
            
            for (int i = 0; i < MAPSIZE * 2; i++)
            {
                DoStuff();
            }

            physicsEnabled = true;
            return 1;
        }

        public void Sunray()
        {
             ushort i = (ushort)(randGen.Next(MAPSIZE - 1));
             ushort k = (ushort)(randGen.Next(MAPSIZE - 1));
            
                    for (ushort j = (ushort)(MAPSIZE-1); j > 0; j--)
                    {
                        if (blockList[i, j, k] == BlockType.Dirt || blockList[i, j, k] == BlockType.Grass)
                        {
                            if(blockListContent[i, j, k, 0] < 150)
                                blockListContent[i, j, k, 0] = blockListContent[i, j, k, 0] + 50;//greenery may reside here
                            break;
                        }
                        else if (blockList[i, j, k] != BlockType.None)
                        {
                            return;
                        }
                    }
        }
        public double Get3DDistance(int x1, int y1, int z1, int x2, int y2, int z2)
        {
            int dx = x2 - x1;
            int dy = y2 - y1;
            int dz = z2 - z1;
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return distance;
        }
        public double Distf(Vector3 x, Vector3 y)
        {
            float dx = y.X - x.X;
            float dy = y.Y - x.Y;
            float dz = y.Z - x.Z;
            float dist = (float)(Math.Sqrt(dx * dx + dy * dy + dz * dz));
            return dist;
        }
        public string GetExplosionPattern(int n)
        {
            string output="";
            int radius = (int)Math.Ceiling((double)varGetI("explosionradius"));
            int size = radius * 2 + 1;
            int center = radius; //Not adding one because arrays start from 0
            for (int z = n; z==n&&z<size; z++)
            {
                ConsoleWrite("Z" + z + ": ");
                output += "Z" + z + ": ";
                for (int x = 0; x < size; x++)
                {
                    string output1 = "";
                    for (int y = 0; y < size; y++)
                    {
                        output1+=tntExplosionPattern[x, y, z] ? "1, " : "0, ";
                    }
                    ConsoleWrite(output1);
                }
                output += "\n";
            }
            return "";
        }

        public void CalculateExplosionPattern()
        {
            int radius = (int)Math.Ceiling((double)varGetI("explosionradius"));
            int size = radius * 2 + 1;
            tntExplosionPattern = new bool[size, size, size];
            int center = radius; //Not adding one because arrays start from 0
            for(int x=0;x<size;x++)
                for(int y=0;y<size;y++)
                    for (int z = 0; z < size; z++)
                    {
                        if (x == y && y == z && z == center)
                            tntExplosionPattern[x, y, z] = true;
                        else
                        {
                            double distance = Get3DDistance(center, center, center, x, y, z);//Use center of blocks
                            if (distance <= (double)varGetI("explosionradius"))
                                tntExplosionPattern[x, y, z] = true;
                            else
                                tntExplosionPattern[x, y, z] = false;
                        }
                    }
        }

        public void status()
        {
            ConsoleWrite(varGetS("name"));//serverName);
            ConsoleWrite(playerList.Count + " / " + varGetI("maxplayers") + " players");
            foreach (string name in varBoolBindings.Keys)
            {
                ConsoleWrite(name + " = " + varBoolBindings[name]);
            }
        }

        public bool Start()
        {
            //Setup the variable toggles
            varBindingsInitialize();
            int tmpMaxPlayers = 16;
            
            // Read in from the config file.
            DatafileWriter dataFile = new DatafileWriter("server.config.txt");
            if (dataFile.Data.ContainsKey("winningcash"))
                winningCashAmount = uint.Parse(dataFile.Data["winningcash"], System.Globalization.CultureInfo.InvariantCulture);
            if (dataFile.Data.ContainsKey("includelava"))
                includeLava = bool.Parse(dataFile.Data["includelava"]);
            if (dataFile.Data.ContainsKey("includewater"))
                includeLava = bool.Parse(dataFile.Data["includewater"]);
            if (dataFile.Data.ContainsKey("orefactor"))
                oreFactor = uint.Parse(dataFile.Data["orefactor"], System.Globalization.CultureInfo.InvariantCulture);
            if (dataFile.Data.ContainsKey("maxplayers"))
                tmpMaxPlayers = (int)Math.Min(32, uint.Parse(dataFile.Data["maxplayers"], System.Globalization.CultureInfo.InvariantCulture));
            if (dataFile.Data.ContainsKey("public"))
                varSet("public", bool.Parse(dataFile.Data["public"]), true);
            if (dataFile.Data.ContainsKey("servername"))
                varSet("name", dataFile.Data["servername"], true);
            if (dataFile.Data.ContainsKey("sandbox"))
                varSet("sandbox", bool.Parse(dataFile.Data["sandbox"]), true);
            if (dataFile.Data.ContainsKey("notnt"))
                varSet("tnt", !bool.Parse(dataFile.Data["notnt"]), true);
            if (dataFile.Data.ContainsKey("sphericaltnt"))
                varSet("stnt", bool.Parse(dataFile.Data["sphericaltnt"]), true);
            if (dataFile.Data.ContainsKey("insane"))
                varSet("insane", bool.Parse(dataFile.Data["insane"]), true);
            if (dataFile.Data.ContainsKey("roadabsorbs"))
                varSet("roadabsorbs", bool.Parse(dataFile.Data["roadabsorbs"]), true);
            if (dataFile.Data.ContainsKey("minelava"))
                varSet("minelava", bool.Parse(dataFile.Data["minelava"]), true);
            if (dataFile.Data.ContainsKey("levelname"))
                levelToLoad = dataFile.Data["levelname"];
            if (dataFile.Data.ContainsKey("greeter"))
                varSet("greeter", dataFile.Data["greeter"],true);

            bool autoannounce = true;
            if (dataFile.Data.ContainsKey("autoannounce"))
                autoannounce = bool.Parse(dataFile.Data["autoannounce"]);

            // Load the ban-list.
            banList = LoadBanList();

            // Load the admin-list
            admins = LoadAdminList();

            if (tmpMaxPlayers>=0)
                varSet("maxplayers", tmpMaxPlayers, true);

            // Initialize the server.
            NetConfiguration netConfig = new NetConfiguration("InfiniminerPlus");
            netConfig.MaxConnections = (int)varGetI("maxplayers");
            netConfig.Port = 5565;
            netServer = new InfiniminerNetServer(netConfig);
            netServer.SetMessageTypeEnabled(NetMessageType.ConnectionApproval, true);

            //netServer.SimulatedMinimumLatency = 0.5f;
           // netServer.SimulatedLatencyVariance = 0.05f;
           // netServer.SimulatedLoss = 0.2f;
           // netServer.SimulatedDuplicates = 0.05f;
            //netServer.Configuration.SendBufferSize = 2048000;
            //netServer.Start();//starts too early
            // Initialize variables we'll use.
            NetBuffer msgBuffer = netServer.CreateBuffer();
            NetMessageType msgType;
            NetConnection msgSender;

            // Store the last time that we did a flow calculation.
            DateTime lastFlowCalc = DateTime.Now;
            DateTime lastFlowCalcZ = DateTime.Now;//temporary
            DateTime sysTimer = DateTime.Now;
            //Check if we should autoload a level
            if (dataFile.Data.ContainsKey("autoload") && bool.Parse(dataFile.Data["autoload"]))
            {
                blockList = new BlockType[MAPSIZE, MAPSIZE, MAPSIZE];
                blockCreatorTeam = new PlayerTeam[MAPSIZE, MAPSIZE, MAPSIZE];
                LoadLevel(levelToLoad);

                lavaBlockCount = 0;
                waterBlockCount = 0;
                int burstBlockCount = 0;

                for (ushort i = 0; i < MAPSIZE; i++)
                    for (ushort j = 0; j < MAPSIZE; j++)
                        for (ushort k = 0; k < MAPSIZE; k++)
                        {
                            if (blockList[i, j, k] == BlockType.Lava)
                            {
                                lavaBlockCount += 1;
                            }
                            else if (blockList[i, j, k] == BlockType.Water)
                            {
                                waterBlockCount += 1;
                            }
                            else if (blockList[i, j, k] == BlockType.MagmaBurst)
                            {
                                burstBlockCount += 1;
                            }
                        }

                ConsoleWrite(waterBlockCount + " water blocks, " + lavaBlockCount + " lava blocks, " + burstBlockCount + " possible bursts." ); 
            }
            else
            {
                // Calculate initial lava flows.
                ConsoleWrite("CALCULATING INITIAL LIQUID BLOCKS");
                newMap();

                lavaBlockCount = 0;
                waterBlockCount = 0;
                int burstBlockCount = 0;
                for (ushort i = 0; i < MAPSIZE; i++)
                    for (ushort j = 0; j < MAPSIZE; j++)
                        for (ushort k = 0; k < MAPSIZE; k++)
                        {
                            if (blockList[i, j, k] == BlockType.Lava)
                            {
                                lavaBlockCount += 1;
                            }
                            else if (blockList[i, j, k] == BlockType.Water)
                            {
                                waterBlockCount += 1;
                            }
                            else if (blockList[i, j, k] == BlockType.MagmaBurst)
                            {
                                burstBlockCount += 1;
                            }
                        }

                ConsoleWrite(waterBlockCount + " water blocks, " + lavaBlockCount + " lava blocks, " + burstBlockCount + " possible bursts.");        
            }
            
            //Caculate the shape of spherical tnt explosions
            CalculateExplosionPattern();

            // Send the initial server list update.
            if (autoannounce)
                PublicServerListUpdate(true);

            lastMapBackup = DateTime.Now;
           
            DateTime lastFPScheck = DateTime.Now;
            double frameRate = 0;
            
            // Main server loop!
            netServer.Start();
            ConsoleWrite("SERVER READY");

            if (!physics.IsAlive)
            {
                ConsoleWrite("Physics thread is limp.");
            }

            while (keepRunning)
            {
                if (!physics.IsAlive)
                {
                    ConsoleWrite("Physics thread died.");
                   // physics.Abort();
                   // physics.Join();
                    //physics.Start();
                }

                frameCount = frameCount + 1;
                if (lastFPScheck <= DateTime.Now - TimeSpan.FromMilliseconds(1000))
                {
                    lastFPScheck = DateTime.Now;
                    frameRate = frameCount;// / gameTime.ElapsedTotalTime.TotalSeconds;
                    
                    if (sleeping == false && frameCount < 20)
                    {
                        ConsoleWrite("Heavy load: " + frameCount + " FPS");
                    }
                    frameCount = 0;
                }
                
                // Process any messages that are here.
                while (netServer.ReadMessage(msgBuffer, out msgType, out msgSender))
                {
                    try
                    {
                        switch (msgType)
                        {
                            case NetMessageType.ConnectionApproval:
                                {
                                    Player newPlayer = new Player(msgSender, null);
                                    newPlayer.Handle = Defines.Sanitize(msgBuffer.ReadString()).Trim();
                                    if (newPlayer.Handle.Length == 0)
                                    {
                                        newPlayer.Handle = "Player";
                                    }

                                    string clientVersion = msgBuffer.ReadString();
                                    if (clientVersion != Defines.INFINIMINER_VERSION)
                                    {
                                        msgSender.Disapprove("VER;" + Defines.INFINIMINER_VERSION);
                                    }
                                    else if (banList.Contains(newPlayer.IP))
                                    {
                                        msgSender.Disapprove("BAN;");
                                    }/*
                                else if (playerList.Count == maxPlayers)
                                {
                                    msgSender.Disapprove("FULL;");
                                }*/
                                    else
                                    {
                                        if (admins.ContainsKey(newPlayer.IP))
                                            newPlayer.admin = admins[newPlayer.IP];
                                        playerList[msgSender] = newPlayer;
                                        //Check if we should compress the map for the client
                                        try
                                        {
                                            bool compression = msgBuffer.ReadBoolean();
                                            if (compression)
                                                playerList[msgSender].compression = true;
                                        } catch { }
                                        toGreet.Add(msgSender);
                                        this.netServer.SanityCheck(msgSender);
                                        msgSender.Approve();
                                        PublicServerListUpdate(true);
                                    }
                                }
                                break;

                            case NetMessageType.StatusChanged:
                                {
                                    if (!this.playerList.ContainsKey(msgSender))
                                    {
                                        break;
                                    }

                                    Player player = playerList[msgSender];

                                    if (msgSender.Status == NetConnectionStatus.Connected)
                                    {
                                        if (sleeping == true)
                                        {
                                            sleeping = false;
                                            physicsEnabled = true;
                                        }
                                        ConsoleWrite("CONNECT: " + playerList[msgSender].Handle + " ( " + playerList[msgSender].IP + " )");
                                        SendCurrentMap(msgSender);
                                        SendPlayerJoined(player);
                                        PublicServerListUpdate();
                                    }

                                    else if (msgSender.Status == NetConnectionStatus.Disconnected)
                                    {
                                        ConsoleWrite("DISCONNECT: " + playerList[msgSender].Handle);
                                        SendPlayerLeft(player, player.Kicked ? "WAS KICKED FROM THE GAME!" : "HAS ABANDONED THEIR DUTIES!");
                                        if (playerList.ContainsKey(msgSender))
                                            playerList.Remove(msgSender);

                                        sleeping = true;
                                        foreach (Player p in playerList.Values)
                                        {
                                            sleeping = false;
                                        }

                                        if (sleeping == true)
                                        {
                                            ConsoleWrite("HIBERNATING");
                                            physicsEnabled = false;
                                        }

                                        PublicServerListUpdate();
                                    }
                                }
                                break;

                            case NetMessageType.Data:
                                {
                                    if (!this.playerList.ContainsKey(msgSender))
                                    {
                                        break;
                                    }

                                    Player player = playerList[msgSender];
                                    InfiniminerMessage dataType = (InfiniminerMessage)msgBuffer.ReadByte();
                                    switch (dataType)
                                    {
                                        case InfiniminerMessage.ChatMessage:
                                            {
                                                // Read the data from the packet.
                                                ChatMessageType chatType = (ChatMessageType)msgBuffer.ReadByte();
                                                string chatString = Defines.Sanitize(msgBuffer.ReadString());
                                                if (!ProcessCommand(chatString,GetAdmin(playerList[msgSender].IP),playerList[msgSender]))
                                                {
                                                    if (chatType == ChatMessageType.SayAll)
                                                    ConsoleWrite("CHAT: (" + player.Handle + ") " + chatString);

                                                    // Append identifier information.
                                                    if (chatType == ChatMessageType.SayAll)
                                                        chatString = player.Handle + " (ALL): " + chatString;
                                                    else
                                                        chatString = player.Handle + " (TEAM): " + chatString;

                                                    // Construct the message packet.
                                                    NetBuffer chatPacket = netServer.CreateBuffer();
                                                    chatPacket.Write((byte)InfiniminerMessage.ChatMessage);
                                                    chatPacket.Write((byte)((player.Team == PlayerTeam.Red) ? ChatMessageType.SayRedTeam : ChatMessageType.SayBlueTeam));
                                                    chatPacket.Write(chatString);

                                                    // Send the packet to people who should recieve it.
                                                    foreach (Player p in playerList.Values)
                                                    {
                                                        if (chatType == ChatMessageType.SayAll ||
                                                            chatType == ChatMessageType.SayBlueTeam && p.Team == PlayerTeam.Blue ||
                                                            chatType == ChatMessageType.SayRedTeam && p.Team == PlayerTeam.Red)
                                                            if (p.NetConn.Status == NetConnectionStatus.Connected)
                                                                netServer.SendMessage(chatPacket, p.NetConn, NetChannel.ReliableInOrder3);
                                                    }
                                                }
                                            }
                                            break;

                                        case InfiniminerMessage.UseTool:
                                            {
                                                Vector3 playerPosition = msgBuffer.ReadVector3();
                                                Vector3 playerHeading = msgBuffer.ReadVector3();
                                                PlayerTools playerTool = (PlayerTools)msgBuffer.ReadByte();
                                                BlockType blockType = (BlockType)msgBuffer.ReadByte();

                                                //getTo
                                                switch (playerTool)
                                                {
                                                    case PlayerTools.Pickaxe:
                                                        UsePickaxe(player, playerPosition, playerHeading);
                                                        break;
                                                    case PlayerTools.StrongArm:
                                                        if (player.Class == PlayerClass.Miner)
                                                        UseStrongArm(player, playerPosition, playerHeading);
                                                        break;
                                                    case PlayerTools.Smash:
                                                        //if(player.Class == PlayerClass.Sapper)
                                                        //UseSmash(player, playerPosition, playerHeading);
                                                        break;
                                                    case PlayerTools.ConstructionGun:
                                                        UseConstructionGun(player, playerPosition, playerHeading, blockType);
                                                        break;
                                                    case PlayerTools.DeconstructionGun:
                                                        UseDeconstructionGun(player, playerPosition, playerHeading);
                                                        break;
                                                    case PlayerTools.ProspectingRadar:
                                                        UseSignPainter(player, playerPosition, playerHeading);
                                                        break;
                                                    case PlayerTools.Detonator:
                                                        if (player.Class == PlayerClass.Sapper)
                                                        UseDetonator(player);
                                                        break;
                                                    case PlayerTools.Remote:
                                                        if (player.Class == PlayerClass.Engineer)
                                                        UseRemote(player);
                                                        break;
                                                    case PlayerTools.SetRemote:
                                                        if (player.Class == PlayerClass.Engineer)
                                                        SetRemote(player);
                                                        break;
                                                    case PlayerTools.ThrowBomb:
                                                        if (player.Class == PlayerClass.Sapper)
                                                        ThrowBomb(player, playerPosition, playerHeading);
                                                        break;
                                                    case PlayerTools.ThrowRope:
                                                        if (player.Class == PlayerClass.Prospector)
                                                            ThrowRope(player, playerPosition, playerHeading);
                                                        break;
                                                    case PlayerTools.Hide:
                                                        if (player.Class == PlayerClass.Prospector)
                                                            Hide(player);
                                                        break;
                                                }
                                            }
                                            break;

                                        case InfiniminerMessage.SelectClass:
                                            {
                                                PlayerClass playerClass = (PlayerClass)msgBuffer.ReadByte();
                                                player.Alive = false;
                                                ConsoleWrite("SELECT_CLASS: " + player.Handle + ", " + playerClass.ToString());
                                                switch (playerClass)
                                                {
                                                    case PlayerClass.Engineer:
                                                        player.Class = playerClass;
                                                        player.OreMax = 200 + (uint)(ResearchComplete[(byte)player.Team, 2] * 20);
                                                        player.WeightMax = 4 + (uint)(ResearchComplete[(byte)player.Team, 2]);
                                                        player.HealthMax = 100 + (uint)(ResearchComplete[(byte)player.Team,1]*20);
                                                        player.Health = player.HealthMax;
                                                        for (int a = 0; a < 100; a++)
                                                        {
                                                            player.Content[a] = 0;
                                                        }
                                                        break;
                                                    case PlayerClass.Miner://strong arm/throws blocks
                                                        player.Class = playerClass;
                                                        player.OreMax = 100 + (uint)(ResearchComplete[(byte)player.Team, 2] * 20);
                                                        player.WeightMax = 10 + (uint)(ResearchComplete[(byte)player.Team, 2]);
                                                        player.HealthMax = 100 + (uint)(ResearchComplete[(byte)player.Team, 1] * 20);
                                                        player.Health = player.HealthMax;
                                                        for (int a = 0; a < 100; a++)
                                                        {
                                                            player.Content[a] = 0;
                                                        }
                                                        break;
                                                    case PlayerClass.Prospector://profiteer/has prospectron/stealth/climb/traps
                                                        player.Class = playerClass;
                                                        player.OreMax = 100 + (uint)(ResearchComplete[(byte)player.Team, 2] * 20);
                                                        player.WeightMax = 6 + (uint)(ResearchComplete[(byte)player.Team, 2]);
                                                        player.HealthMax = 100 + (uint)(ResearchComplete[(byte)player.Team, 1] * 20);
                                                        player.Health = player.HealthMax;
                                                        for (int a = 0; a < 100; a++)
                                                        {
                                                            player.Content[a] = 0;
                                                        }
                                                        break;
                                                    case PlayerClass.Sapper://berserker/charge that knocks people and blocks away/repairs block
                                                        player.Class = playerClass;
                                                        player.OreMax = 100 + (uint)(ResearchComplete[(byte)player.Team, 2] * 20);
                                                        player.WeightMax = 4 + (uint)(ResearchComplete[(byte)player.Team, 2]);
                                                        player.HealthMax = 100 + (uint)(ResearchComplete[(byte)player.Team, 1] * 20);
                                                        player.Health = player.HealthMax;
                                                        for (int a = 0; a < 100; a++)
                                                        {
                                                            player.Content[a] = 0;
                                                        }
                                                        break;
                                                }
                                                SendResourceUpdate(player);
                                                SendContentUpdate(player);
                                                SendPlayerSetClass(player);
                                            }
                                            break;

                                        case InfiniminerMessage.PlayerSetTeam:
                                            {
                                                PlayerTeam playerTeam = (PlayerTeam)msgBuffer.ReadByte();
                                                ConsoleWrite("SELECT_TEAM: " + player.Handle + ", " + playerTeam.ToString());
                                                player.Team = playerTeam;
                                                player.Health = 0;
                                                player.Alive = false;
                                                Player_Dead(player, "");
                                                SendResourceUpdate(player);
                                                SendPlayerSetTeam(player);
                                            }
                                            break;

                                        case InfiniminerMessage.PlayerDead:
                                            {
                                                string deathMessage = msgBuffer.ReadString();
                                                if (player.Alive)
                                                {
                                                    Player_Dead(player, deathMessage);
                                                }
                                            }
                                            break;

                                        case InfiniminerMessage.PlayerAlive:
                                            {
                                                if (toGreet.Contains(msgSender))
                                                {
                                                    string greeting = varGetS("greeter");
                                                    greeting = greeting.Replace("[name]", playerList[msgSender].Handle);
                                                    if (greeting != "")
                                                    {
                                                        NetBuffer greetBuffer = netServer.CreateBuffer();
                                                        greetBuffer.Write((byte)InfiniminerMessage.ChatMessage);
                                                        greetBuffer.Write((byte)ChatMessageType.SayAll);
                                                        greetBuffer.Write(Defines.Sanitize(greeting));
                                                        netServer.SendMessage(greetBuffer, msgSender, NetChannel.ReliableInOrder3);
                                                    }
                                                    toGreet.Remove(msgSender);
                                                }
                                                ConsoleWrite("PLAYER_ALIVE: " + player.Handle);
                                                player.Ore = 0;
                                                player.Cash = 0;
                                                player.Weight = 0;
                                                player.Health = player.HealthMax;
                                                player.Alive = true;
                                                player.respawnTimer = DateTime.Now + TimeSpan.FromSeconds(5);
                                                SendResourceUpdate(player);
                                                SendPlayerAlive(player);
                                            }
                                            break;
                                        case InfiniminerMessage.PlayerRespawn:
                                            {
                                                SendPlayerRespawn(player);//new respawn
                                            }
                                            break;
                                        case InfiniminerMessage.PlayerUpdate:
                                            {
                                                if (player.Alive)
                                                {
                                                    player.Position = Auth_Position(msgBuffer.ReadVector3(), player, true);
                                                    player.Heading = Auth_Heading(msgBuffer.ReadVector3());
                                                    player.Tool = (PlayerTools)msgBuffer.ReadByte();
                                                    player.UsingTool = msgBuffer.ReadBoolean();
                                                    SendPlayerUpdate(player);
                                                }
                                            }
                                            break;
                                        case InfiniminerMessage.PlayerSlap:
                                            {
                                                if (player.Alive)
                                                {
                                                    if (player.playerToolCooldown > DateTime.Now)
                                                    {
                                                        break;//discard fast packet
                                                    }

                                                    player.Position = Auth_Position(msgBuffer.ReadVector3(), player, true);
                                                    player.Heading = Auth_Heading(msgBuffer.ReadVector3());
                                                    player.Tool = (PlayerTools)msgBuffer.ReadByte();
                                                    player.UsingTool = true;
                                                    Auth_Slap(player, msgBuffer.ReadUInt32());
                                                    SendPlayerUpdate(player);

                                                    player.playerToolCooldown = DateTime.Now + TimeSpan.FromSeconds((float)(player.GetToolCooldown(PlayerTools.Pickaxe)));

                                                    if (player.Class == PlayerClass.Prospector && player.Content[5] > 0)//reveal when hit
                                                    {
                                                        player.Content[6] = 0;//uncharge
                                                        player.Content[1] = 0;//reappear on radar
                                                        SendPlayerContentUpdate(player, 1);
                                                        player.Content[5] = 0;//sight
                                                        SendContentSpecificUpdate(player, 5);
                                                        SendContentSpecificUpdate(player, 6);
                                                        SendPlayerContentUpdate(player, 5);
                                                        SendServerMessageToPlayer("You have been revealed!", player.NetConn);
                                                        EffectAtPoint(player.Position - Vector3.UnitY * 1.5f, 1);
                                                    }
                                                }
                                            }
                                            break;
                                        case InfiniminerMessage.PlayerUpdate1://minus position
                                            {
                                                if (player.Alive)
                                                {
                                                    player.Heading = Auth_Heading(msgBuffer.ReadVector3());
                                                    player.Tool = (PlayerTools)msgBuffer.ReadByte();
                                                    player.UsingTool = msgBuffer.ReadBoolean();
                                                    SendPlayerUpdate(player);
                                                }
                                            }
                                            break;
                                        case InfiniminerMessage.PlayerUpdate2://minus position and heading
                                            {
                                                if (player.Alive)
                                                {
                                                    player.Tool = (PlayerTools)msgBuffer.ReadByte();
                                                    player.UsingTool = msgBuffer.ReadBoolean();
                                                    SendPlayerUpdate(player);
                                                }
                                            }
                                            break;
                                        case InfiniminerMessage.PlayerHurt://client speaks of fall damage
                                            {
                                                uint newhp = msgBuffer.ReadUInt32();
                                                if (newhp < player.Health)
                                                {
                                                    if (player.Team == PlayerTeam.Red)
                                                    {
                                                        DebrisEffectAtPoint((int)(player.Position.X), (int)(player.Position.Y), (int)(player.Position.Z), BlockType.SolidRed, 10 + (int)(player.Health - newhp));
                                                    }
                                                    else
                                                    {
                                                        DebrisEffectAtPoint((int)(player.Position.X), (int)(player.Position.Y), (int)(player.Position.Z), BlockType.SolidBlue, 10 + (int)(player.Health - newhp));
                                                    }

                                                    player.Health = newhp;
                                                    if (player.Health < 1)
                                                    {
                                                        Player_Dead(player, "FELL TO THEIR DEATH!");
                                                    }
                                                }
                                            }
                                            break;
                                        case InfiniminerMessage.PlayerPosition://server not interested in clients complaints about position
                                            {
                                              
                                            }
                                            break;
                                        case InfiniminerMessage.PlayerInteract://client speaks of mashing on block
                                            {
                                                player.Position = Auth_Position(msgBuffer.ReadVector3(), player, true);

                                                uint btn = msgBuffer.ReadUInt32();
                                                uint btnx = msgBuffer.ReadUInt32();
                                                uint btny = msgBuffer.ReadUInt32();
                                                uint btnz = msgBuffer.ReadUInt32();

                                                //if (blockList[btnx, btny, btnz] == BlockType.Pump || blockList[btnx, btny, btnz] == BlockType.Pipe || blockList[btnx, btny, btnz] == BlockType.Generator || blockList[btnx, btny, btnz] == BlockType.Barrel || blockList[btnx, btny, btnz] == BlockType.Switch)
                                                //{
                                                    if (Get3DDistance((int)btnx, (int)btny, (int)btnz, (int)player.Position.X, (int)player.Position.Y, (int)player.Position.Z) < 4)
                                                    {
                                                        PlayerInteract(player,btn, btnx, btny, btnz);
                                                    }
                                                //}
                                            }
                                            break;
                                        case InfiniminerMessage.DepositOre:
                                            {
                                                DepositOre(player);
                                                foreach (Player p in playerList.Values)
                                                    SendResourceUpdate(p);
                                            }
                                            break;

                                        case InfiniminerMessage.WithdrawOre:
                                            {
                                                WithdrawOre(player);
                                                foreach (Player p in playerList.Values)
                                                    SendResourceUpdate(p);
                                            }
                                            break;

                                        case InfiniminerMessage.PlayerPing:
                                            {
                                                if (player.Ping == 0)
                                                {
                                                    SendPlayerPing((uint)msgBuffer.ReadInt32());
                                                    player.Ping = 2;
                                                }
                                            }
                                            break;

                                        case InfiniminerMessage.PlaySound:
                                            {
                                                InfiniminerSound sound = (InfiniminerSound)msgBuffer.ReadByte();
                                                Vector3 position = msgBuffer.ReadVector3();
                                                PlaySoundForEveryoneElse(sound, position,player);
                                            }
                                            break;

                                        case InfiniminerMessage.DropItem:
                                            {
                                                DropItem(player, msgBuffer.ReadUInt32());
                                            }
                                            break;

                                        case InfiniminerMessage.GetItem:
                                            {
                                                //verify players position before get
                                                player.Position = Auth_Position(msgBuffer.ReadVector3(), player, false);
                                                
                                                GetItem(player,msgBuffer.ReadUInt32());
                                            }
                                            break;
                                    }
                                }
                                break;
                        }
                    }
                    catch { }
                }

                //Time to backup map?
                TimeSpan mapUpdateTimeSpan = DateTime.Now - lastMapBackup;
                if (mapUpdateTimeSpan.TotalMinutes > 5)
                {
                    lastMapBackup = DateTime.Now;
                    SaveLevel("autoBK.lvl");
                }

                // Time to send a new server update?
                PublicServerListUpdate(); //It checks for public server / time span

                //Time to terminate finished map sending threads?
                TerminateFinishedThreads();

                // Check for players who are in the zone to deposit.
                VictoryCheck();
                    
                // Is it time to do a lava calculation? If so, do it!
                TimeSpan timeSpan = DateTime.Now - sysTimer;
                if (timeSpan.TotalMilliseconds > 2000)
                {
                    //ConsoleWrite("" + delta);
                    sysTimer = DateTime.Now;

                    //secondflow += 1;

                    //if (secondflow > 2)//every 2nd flow, remove the vacuum that prevent re-spread
                    //{
                    //    EraseVacuum();
                    //    secondflow = 0;
                    //}
                    if (randGen.Next(1, 4) == 3)
                    {
                        bool isUpdateOre = false;
                        bool isUpdateCash = false;
                        for (int a = 1; a < 3; a++)
                        {
                            if (artifactActive[a, 1] > 0)//material artifact
                            {
                                isUpdateOre = true;
                                if (a == 1)
                                {
                                    teamOreRed = teamOreRed + (uint)(10 * artifactActive[a, 1]);
                                }
                                else if (a == 2)
                                {
                                    teamOreBlue = teamOreBlue + (uint)(10 * artifactActive[a, 1]);
                                }

                            }
                            if (artifactActive[a, 5] > 0)//golden artifact
                            {
                                isUpdateCash = true;
                                if (a == 1)
                                {
                                    teamCashRed = teamCashRed + (uint)(2 * artifactActive[a, 5]);
                                }
                                else if (a == 2)
                                {
                                    teamCashBlue = teamCashBlue + (uint)(2 * artifactActive[a, 5]);
                                }

                            }
                        }

                        if (isUpdateOre)
                            foreach (Player p in playerList.Values)
                                SendTeamOreUpdate(p);

                        if(isUpdateCash)
                        foreach (Player p in playerList.Values)
                            SendTeamCashUpdate(p);
                    }
                    foreach (Player p in playerList.Values)//regeneration
                    {
                        if (p.Ping > 0)
                            p.Ping--;

                        if (p.Alive)
                        {
                            if (p.Content[10] == 1)//material artifact personal
                            {
                                if (randGen.Next(1, 4) == 3)
                                {
                                    if (p.Ore < p.OreMax)
                                    {
                                        p.Ore += 10;
                                        if (p.Ore >= p.OreMax)
                                            p.Ore = p.OreMax;

                                        SendOreUpdate(p);
                                    }
                                }
                            }
                            else if (p.Content[10] == 5)//golden artifact personal
                            {
                                if (p.Ore > 99)
                                {
                                    if (p.Weight < p.WeightMax)
                                    {
                                        p.Weight++;
                                        p.Cash += 10;
                                        p.Ore -= 100;
                                        SendCashUpdate(p);
                                        SendWeightUpdate(p);
                                        SendOreUpdate(p);
                                        PlaySound(InfiniminerSound.CashDeposit, p.Position);
                                    }
                                }
                            }
                            else if (p.Content[10] == 6)//storm artifact personal
                            {

                                if(artifactActive[(byte)((p.Team == PlayerTeam.Red) ? PlayerTeam.Blue : PlayerTeam.Red),6] == 0)//stored storm artifact makes team immune
                                foreach (Player pt in playerList.Values)
                                {
                                    if (p.Team != pt.Team && pt.Alive)
                                    {
                                        float distfromPlayer = (p.Position - pt.Position).Length();
                                        if (distfromPlayer < 5)
                                        {
                                            pt.Health -= 5;
                                            if (pt.Health <= 0)
                                            {
                                                Player_Dead(pt,"WAS SHOCKED!");
                                            }
                                            else
                                                SendHealthUpdate(pt);

                                            EffectAtPoint(pt.Position, 1);
                                        }
                                    }
                                }
                            }

                            if (p.Health >= p.HealthMax)
                            {
                                p.Health = p.HealthMax;
                            }
                            else
                            {
                                p.Health = (uint)(p.Health + teamRegeneration[(byte)p.Team]);
                                if (p.Content[10] == 3)//regeneration artifact
                                {
                                    p.Health += 4;
                                }

                                if (p.Health >= p.HealthMax)
                                {
                                    p.Health = p.HealthMax;
                                }
                                SendHealthUpdate(p);
                            }
                            
                            if (p.Class == PlayerClass.Prospector)
                            {
                                if (p.Content[5] == 1)
                                {
                                    p.Content[6]--;
                                    if (p.Content[6] < 1)
                                    {
                                        p.Content[1] = 0;
                                        SendPlayerContentUpdate(p, 1);
                                        p.Content[5] = 0;//sight
                                        SendContentSpecificUpdate(p, 5);
                                        SendPlayerContentUpdate(p, 5);
                                        SendServerMessageToPlayer("Hide must now recharge!", p.NetConn);
                                        EffectAtPoint(p.Position - Vector3.UnitY * 1.5f, 1);
                                    }
                                }
                                else
                                {
                                    if(p.Content[6] < 4)
                                        p.Content[6]++;
                                }
                            }

                            //if (p.Class == PlayerClass.Prospector)//temperature data//giving everyone
                            //{
                            //    p.Content[6] = 0;
                            //    for(int a = -5;a < 6;a++)
                            //        for(int b = -5;b < 6;b++)
                            //            for (int c = -5; c < 6; c++)
                            //            {
                            //                int nx = a + (int)p.Position.X;
                            //                int ny = b + (int)p.Position.Y;
                            //                int nz = c + (int)p.Position.Z;
                            //                if (nx < MAPSIZE - 1 && ny < MAPSIZE - 1 && nz < MAPSIZE - 1 && nx > 0 && ny > 0 && nz > 0)
                            //                {
                            //                    BlockType block = blockList[nx,ny,nz];
                            //                    if (block == BlockType.Lava || block == BlockType.MagmaBurst || block == BlockType.MagmaVent)
                            //                    {
                            //                        p.Content[6] += 5 - Math.Abs(a) + 5 - Math.Abs(b) + 5 - Math.Abs(c);
                            //                    }
                            //                }
                            //            }

                            //    if (p.Content[6] > 0)
                            //        SendContentSpecificUpdate(p, 6);
                            //}
                        }
                    }
                }

                TimeSpan timeSpanZ = DateTime.Now - lastFlowCalcZ;
                serverTime[timeQueue] = DateTime.Now - lastTime;//timeQueue

                timeQueue += 1;
                if (timeQueue > 19)
                    timeQueue = 0;

                lastTime = DateTime.Now;
                delta = (float)((serverTime[0].TotalSeconds + serverTime[1].TotalSeconds + serverTime[2].TotalSeconds + serverTime[3].TotalSeconds + serverTime[4].TotalSeconds + serverTime[5].TotalSeconds + serverTime[6].TotalSeconds + serverTime[7].TotalSeconds + serverTime[8].TotalSeconds + serverTime[9].TotalSeconds + serverTime[10].TotalSeconds + serverTime[11].TotalSeconds + serverTime[12].TotalSeconds + serverTime[13].TotalSeconds + serverTime[14].TotalSeconds + serverTime[15].TotalSeconds + serverTime[16].TotalSeconds + serverTime[17].TotalSeconds + serverTime[18].TotalSeconds + serverTime[19].TotalSeconds) / 20);
                Sunray();
                if (timeSpanZ.TotalMilliseconds > 50)
                {

                    lastFlowCalcZ = DateTime.Now;
                    DoItems();

                }
                //random diamond appearance
                if (sleeping == false)
                if (randGen.Next(1, 100000) == 2)
                {
                    ushort diamondx = (ushort)randGen.Next(4, 57);
                    ushort diamondy = (ushort)randGen.Next(3, 30);
                    ushort diamondz = (ushort)randGen.Next(4, 57);

                    if (blockList[diamondx, diamondy, diamondz] == BlockType.Dirt)
                    {
                       // ConsoleWrite("diamond spawned at " + diamondx + "/" + diamondy + "/" + diamondz);
                        SetBlock(diamondx, diamondy, diamondz, BlockType.Diamond, PlayerTeam.None);
                        blockListHP[diamondx, diamondy, diamondz] = BlockInformation.GetMaxHP(BlockType.Diamond);
                    }
                }
                // Handle console keypresses.
                while (Console.KeyAvailable)
                {
                    ConsoleKeyInfo keyInfo = Console.ReadKey();
                    if (keyInfo.Key == ConsoleKey.Enter)
                    {
                        if (consoleInput.Length > 0)
                            ConsoleProcessInput();
                    }
                    else if (keyInfo.Key == ConsoleKey.Backspace)
                    {
                        if (consoleInput.Length > 0)
                            consoleInput = consoleInput.Substring(0, consoleInput.Length - 1);
                        ConsoleRedraw();
                    }
                    else
                    {
                        consoleInput += keyInfo.KeyChar;
                        ConsoleRedraw();
                    }
                }

                // Is the game over?
                if (winningTeam != PlayerTeam.None && !restartTriggered)
                {
                    BroadcastGameOver();
                    restartTriggered = true;
                    restartTime = DateTime.Now.AddSeconds(10);
                }

                // Restart the server?
                if (restartTriggered && DateTime.Now > restartTime)
                {
                    SaveLevel("autosave_" + (UInt64)DateTime.Now.ToBinary() + ".lvl");

                    netServer.Shutdown("The server is restarting.");
                    
                    Thread.Sleep(100);

                    physics.Abort();
                   // mechanics.Abort();
                    return true;//terminates server thread completely
                }

                // Pass control over to waiting threads.
                if(sleeping == true) {
                    Thread.Sleep(50);
                }
                else
                {
                    Thread.Sleep(1);
                }
            }

            MessageAll("Server going down NOW!");

            netServer.Shutdown("The server was terminated.");
            return false;
        }

        public void VictoryCheck()
        {
            //foreach (Player p in playerList.Values)
            //{
              //  if (p.Position.Y > 64 - Defines.GROUND_LEVEL)
             //       DepositCash(p);
           // }

            if (varGetB("sandbox"))
                return;
            if (teamArtifactsBlue >= winningCashAmount && winningTeam == PlayerTeam.None)
                winningTeam = PlayerTeam.Blue;
            if (teamArtifactsRed >= winningCashAmount && winningTeam == PlayerTeam.None)
                winningTeam = PlayerTeam.Red;
        }

        public void EraseVacuum()
        {
            for (ushort i = 0; i < MAPSIZE; i++)
                for (ushort j = 0; j < MAPSIZE; j++)
                    for (ushort k = 0; k < MAPSIZE; k++)
                        if (blockList[i, j, k] == BlockType.Vacuum)
                        {
                            blockList[i, j, k] = BlockType.None;
                        }
        }

        public void DoMechanics()//seems to crash from time to time? probably due to item creation while thread is processing
        {
            DateTime lastFlowCalc = DateTime.Now;

            while (1 == 1)
            {
                while (physicsEnabled)
                {
                    TimeSpan timeSpan = DateTime.Now - lastFlowCalc;
                    serverTime[timeQueue] = DateTime.Now - lastTime;//timeQueue

                    timeQueue += 1;
                    if (timeQueue > 19)
                        timeQueue = 0;

                    lastTime = DateTime.Now;
                    delta = (float)((serverTime[0].TotalSeconds + serverTime[1].TotalSeconds + serverTime[2].TotalSeconds + serverTime[3].TotalSeconds + serverTime[4].TotalSeconds + serverTime[5].TotalSeconds + serverTime[6].TotalSeconds + serverTime[7].TotalSeconds + serverTime[8].TotalSeconds + serverTime[9].TotalSeconds + serverTime[10].TotalSeconds + serverTime[11].TotalSeconds + serverTime[12].TotalSeconds + serverTime[13].TotalSeconds + serverTime[14].TotalSeconds + serverTime[15].TotalSeconds + serverTime[16].TotalSeconds + serverTime[17].TotalSeconds + serverTime[18].TotalSeconds + serverTime[19].TotalSeconds) / 20);

                    if (timeSpan.TotalMilliseconds > 50)
                    {

                        lastFlowCalc = DateTime.Now;
                        DoItems();

                    }
                    Thread.Sleep(1);
                }
                Thread.Sleep(50);
            }
        }
        public void DoPhysics()
        {
            DateTime lastFlowCalc = DateTime.Now;

            while (1==1)
            {
                while (physicsEnabled)
                {
                    TimeSpan timeSpan = DateTime.Now - lastFlowCalc;

                    if (timeSpan.TotalMilliseconds > 400)
                    {

                        lastFlowCalc = DateTime.Now;
                        DoStuff();

                    }
                    Thread.Sleep(2);
                }
                Thread.Sleep(50);
            }
        }

        public void DoItems()
        {
            Vector3 tv = Vector3.Zero;
            Vector3 tvv = Vector3.Zero;
        
            float GRAVITY = 0.1f;

            for (int a = highestitem; a >= 0; a--)
            {
                if(itemList.ContainsKey((uint)(a)))
                {
                    Item i = itemList[(uint)(a)];
                    if (i.Type == ItemType.Bomb)
                    {
                        i.Content[5]--;

                        if (i.Content[5] == 1)
                        {
                            BombAtPoint((int)(i.Position.X), (int)(i.Position.Y), (int)(i.Position.Z), (PlayerTeam)i.Content[6]);
                            i.Disposing = true;
                            continue;
                        }
                    }
                    else if (i.Type == ItemType.Artifact)
                    {
                        if (i.Content[6] == 0)//not locked
                        {
                            if (i.Content[10] == 3)//regeneration artifact
                            {
                                if (randGen.Next(1, 25) == 10)
                                {
                                    int maxhp;
                                    for (int ax = -2 + (int)i.Position.X; ax < 3 + (int)i.Position.X; ax++)
                                        for (int ay = -2 + (int)i.Position.Y; ay < 3 + (int)i.Position.Y; ay++)
                                            for (int az = -2 + (int)i.Position.Z; az < 3 + (int)i.Position.Z; az++)
                                            {
                                                if (ax < MAPSIZE - 1 && ay < MAPSIZE - 1 && az < MAPSIZE - 1 && ax > 0 && ay > 0 && az > 0)
                                                {
                                                    maxhp = BlockInformation.GetMaxHP(blockList[ax, ay, az]);
                                                    if (maxhp > 1)
                                                        if (blockList[ax, ay, az] != BlockType.Gold && blockList[ax, ay, az] != BlockType.Diamond)
                                                        {
                                                            if (blockListHP[ax, ay, az] < maxhp)
                                                            {
                                                                blockListHP[ax, ay, az]++;

                                                                if (blockListHP[ax, ay, az] > maxhp)//will not fortify
                                                                    blockListHP[ax, ay, az] = maxhp;
                                                            }
                                                        }
                                                }
                                            }
                                 }
                            }
                            else if (i.Content[10] == 6)//storm artifact
                            {
                                if (randGen.Next(1, 20) == 10 && i.Content[11] < 30)
                                {
                                    int ax = randGen.Next(3) - 1;
                                    int ay = randGen.Next(2) + 1;
                                    int az = randGen.Next(3) - 1;

                                    if(BlockAtPoint(new Vector3(ax + i.Position.X, ay + i.Position.Y, az + i.Position.Z)) == BlockType.None)
                                    {
                                        i.Content[11]++;
                                        SetBlock((ushort)(ax + i.Position.X), (ushort)(ay + i.Position.Y), (ushort)(az + i.Position.Z), BlockType.Water, PlayerTeam.None);
                                    }
                                }
                            }
                            else if (i.Content[10] == 7)//reflection artifact
                            {

                            }
                        }
                        else
                        {
                        }
                    }

                    tv = i.Position;
                    tv.Y -= 0.05f;//changes where the item rests

                    if (BlockAtPoint(tv + i.Velocity * (delta * 50)) == BlockType.None || BlockAtPoint(tv + i.Velocity * (delta * 50)) == BlockType.Water)//shouldnt be checking every 100ms, needs area check
                    {
                        i.Velocity.Y -= GRAVITY*i.Weight;// *(delta * 50);//delta interferes with sleep states
                        i.Position += i.Velocity * (delta * 50);
                        //i.Velocity.X = i.Velocity.X * 0.99f;
                        //i.Velocity.Z = i.Velocity.Z * 0.99f;
                        SendItemUpdate(i);

                        if (i.Position.Y < -50)//fallen off map
                        {
                            i.Disposing = true;
                        }


                    }
                    else if (i.Velocity.X != 0.0f || i.Velocity.Y != 0.0f || i.Velocity.Z != 0.0f)
                    {
                        Vector3 nv = i.Velocity;//adjustment axis
                        nv.Y = i.Velocity.Y;
                        nv.X = 0;
                        nv.Z = 0;
                        if (Math.Abs(i.Velocity.Y) > 0.5f)
                        {
                            if (BlockAtPoint(tv + nv) != BlockType.None || BlockAtPoint(tv + nv) != BlockType.Water)
                            {
                                i.Velocity.Y = -i.Velocity.Y / 2;
                                if (i.Type == ItemType.Rope)
                                {
                                    i.Velocity = Vector3.Zero;
                                }
                                continue;
                            }
                        }
                        else
                        {
                            i.Velocity.Y = 0;
                        }

                        nv.X = i.Velocity.X;
                        nv.Y = 0;
                        nv.Z = 0;
                        if (Math.Abs(i.Velocity.X) > 0.2f)
                        {
                            if (BlockAtPoint(tv + nv) != BlockType.None || BlockAtPoint(tv + nv) != BlockType.Water)
                            {
                                i.Velocity.X = -i.Velocity.X / 2;
                                if (i.Type == ItemType.Rope)
                                {
                                    i.Velocity = Vector3.Zero;
                                }
                                continue;
                            }
                        }
                        else
                        {
                            i.Velocity.X = 0;
                        }


                        nv.X = 0;
                        nv.Y = 0;
                        nv.Z = i.Velocity.Z;
                        if (Math.Abs(i.Velocity.Z) > 0.2f)
                        {
                            if (BlockAtPoint(tv + nv) != BlockType.None || BlockAtPoint(tv + nv) != BlockType.Water)
                            {
                                i.Velocity.Z = -i.Velocity.Z / 2;
                                if (i.Type == ItemType.Rope)
                                {
                                    i.Velocity = Vector3.Zero;
                                }
                                continue;
                            }
                        }
                        else
                        {
                            i.Velocity.Z = 0;
                        }
                    }
                    else
                    {
                        //item no longer needs to move
                    }
                }
            }

           /* foreach (KeyValuePair<uint, Item> i in itemList)
            {

                if (i.Value.Type == ItemType.Bomb)
                {
                    i.Value.Content[5]--;

                    if (i.Value.Content[5] == 1)
                    {
                        BombAtPoint((int)(i.Value.Position.X), (int)(i.Value.Position.Y), (int)(i.Value.Position.Z));
                        i.Value.Disposing = true;
                        continue;
                    }
                }
                tv = i.Value.Position;
                tv.Y -= 0.2f;//changes where the item rests
                
                if (BlockAtPoint(tv + i.Value.Velocity * (delta*50)) == BlockType.None)//shouldnt be checking every 100ms, needs area check
                {
                    i.Value.Velocity.Y -= GRAVITY;// *(delta * 50);//delta interferes with sleep states
                    i.Value.Position += i.Value.Velocity * (delta*50);
                    //i.Value.Velocity.X = i.Value.Velocity.X * 0.99f;
                    //i.Value.Velocity.Z = i.Value.Velocity.Z * 0.99f;
                    SendItemUpdate(i.Value);

                    if (i.Value.Position.Y < -50)//fallen off map
                    {
                        i.Value.Disposing = true;
                    }
                   

                }
                else if (i.Value.Velocity.X != 0.0f || i.Value.Velocity.Y != 0.0f || i.Value.Velocity.Z != 0.0f)
                {

                    Vector3 nv = i.Value.Velocity;//adjustment axis
                    nv.Y = i.Value.Velocity.Y;
                    nv.X = 0;
                    nv.Z = 0;
                    if (Math.Abs(i.Value.Velocity.Y) > 0.5f)
                    {
                        if (BlockAtPoint(tv + nv) != BlockType.None)
                        {
                            i.Value.Velocity.Y = -i.Value.Velocity.Y / 2;
                        }
                    }
                    else
                    {
                        i.Value.Velocity.Y = 0;
                    }

                    nv.X = i.Value.Velocity.X;
                    nv.Y = 0;
                    nv.Z = 0;
                    if (Math.Abs(i.Value.Velocity.X) > 0.5f)
                    {
                        if (BlockAtPoint(tv + nv) != BlockType.None)
                        {
                            i.Value.Velocity.X = -i.Value.Velocity.X / 2;
                        }
                    }
                    else
                    {
                        i.Value.Velocity.X = 0;
                    }


                    nv.X = 0;
                    nv.Y = 0;
                    nv.Z = i.Value.Velocity.Z;
                    if (Math.Abs(i.Value.Velocity.Z) > 0.5f)
                    {
                        if (BlockAtPoint(tv + nv) != BlockType.None)
                        {
                            i.Value.Velocity.Z = -i.Value.Velocity.Z / 2;
                        }
                    }
                    else
                    {
                        i.Value.Velocity.Z = 0;
                    }
                }
                else
                {
                   //item no longer needs to move
                }
                
            }*/
           
            foreach (KeyValuePair<uint, Item> i in itemList)//depreciated since no longer using threads
            {
                if (i.Value.Disposing)
                {
                    DeleteItem(i.Key);
                    break;
                }
            }
        }
        public void DoStuff()
        {
            frameid += 1;//make unique id to prevent reprocessing gravity
            //volcano frequency
            if (1==0)//randGen.Next(1, 500) == 1 && physicsEnabled)
            {
                bool volcanospawn = true;
                while (volcanospawn == true)
                {
                    int vx = randGen.Next(8, 52);
                    int vy = randGen.Next(4, 50);
                    int vz = randGen.Next(8, 52);

                    if (blockList[vx, vy, vz] != BlockType.Lava || blockList[vx, vy, vz] != BlockType.Spring || blockList[vx, vy, vz] != BlockType.MagmaVent || blockList[vx, vy, vz] != BlockType.Rock)//Fire)//volcano testing
                    {
                        if (blockList[vx, vy+1, vz] != BlockType.Lava || blockList[vx, vy+1, vz] != BlockType.Spring || blockList[vx, vy+1, vz] != BlockType.MagmaVent || blockList[vx, vy+1, vz] != BlockType.Rock)//Fire)//volcano testing
                        {
                            volcanospawn = false;
                            int vmag = randGen.Next(30, 60);
                            ConsoleWrite("Volcanic eruption at " + vx + ", " + vy + ", " + vz + " Magnitude: "+ vmag);
                            SetBlock((ushort)(vx), (ushort)(vy), (ushort)(vz), BlockType.Lava, PlayerTeam.None);//magma cools down into dirt
                            blockListContent[vx, vy, vz, 0] = vmag;//volcano strength
                            blockListContent[vx, vy, vz, 1] = 960;//temperature
                            EarthquakeEffectAtPoint(vx, vy, vz, vmag);
                        }
                    }
                }
            }

            for (ushort i = 0; i < MAPSIZE; i++)
                for (ushort j = 0; j < MAPSIZE; j++)
                    for (ushort k = 0; k < MAPSIZE; k++)
                    {
                        //gravity //needs to readd the block for processing, its missing on certain gravity changes
                        if (blockListContent[i, j, k, 10] > 0)
                        if (frameid != blockListContent[i, j, k, 10])
                        {// divide acceleration vector by 100 to create ghetto float vector
                            Vector3 newpoint = new Vector3((float)(blockListContent[i, j, k, 14] + blockListContent[i, j, k, 11]) / 100, (float)(blockListContent[i, j, k, 15] + blockListContent[i, j, k, 12]) / 100, (float)(blockListContent[i, j, k, 16] + blockListContent[i, j, k, 13]) / 100);
                            
                            ushort nx = (ushort)(newpoint.X);
                            ushort ny = (ushort)(newpoint.Y);
                            ushort nz = (ushort)(newpoint.Z);

                            blockListContent[i, j, k, 10] = 0;

                            if (nx < MAPSIZE - 1 && ny < MAPSIZE - 1 && nz < MAPSIZE - 1 && nx > 0 && ny > 0 && nz > 0)
                            {
                                if (BlockAtPoint(newpoint) == BlockType.None && blockList[i, j, k] != BlockType.None)
                                {
                                    SetBlock(nx, ny, nz, blockList[i, j, k], blockCreatorTeam[i, j, k]);
                                    for (ushort c = 0; c < 14; c++)//copy content from 0-13
                                    {
                                        blockListContent[nx, ny, nz, c] = blockListContent[i, j, k, c];

                                    }
                                    blockListContent[nx, ny, nz, 10] = frameid;

                                    if (blockListContent[nx, ny, nz, 12] > -50)//stop gravity from overflowing and skipping tiles
                                        blockListContent[nx, ny, nz, 12] = (int)((float)(blockListContent[nx, ny, nz, 12] - 50.0f));
                                    else
                                    {
                                        blockListContent[nx, ny, nz, 12] = -100;
                                    }

                                    blockListContent[nx, ny, nz, 14] = (int)(newpoint.X * 100);
                                    blockListContent[nx, ny, nz, 15] = (int)(newpoint.Y * 100);
                                    blockListContent[nx, ny, nz, 16] = (int)(newpoint.Z * 100);

                                    if (blockList[i, j, k] == BlockType.Explosive)//explosive list for tnt update
                                    {
                                        if (blockListContent[i, j, k, 17] == 0)//create owner
                                        {
                                            foreach (Player p in playerList.Values)
                                            {
                                                int cc = p.ExplosiveList.Count;

                                                int ca = 0;
                                                while (ca < cc)
                                                {
                                                    if (p.ExplosiveList[ca].X == i && p.ExplosiveList[ca].Y == j && p.ExplosiveList[ca].Z == k)
                                                    {
                                                        p.ExplosiveList.RemoveAt(ca);
                                                        blockListContent[i, j, k, 17] = (int)(p.ID);
                                                        break;
                                                    }
                                                    ca += 1;
                                                }
                                            }
                                        }

                                        if (blockListContent[i, j, k, 17] > 0)
                                        {
                                            foreach (Player p in playerList.Values)
                                            {
                                                if (p.ID == (uint)(blockListContent[i, j, k, 17]))
                                                {
                                                    //found explosive this belongs to
                                                    p.ExplosiveList.Add(new Vector3(nx, ny, nz));
                                                    blockListContent[nx, ny, nz, 17] = blockListContent[i, j, k, 17];
                                                    p.ExplosiveList.Remove(new Vector3(i, j, k));
                                                    blockListContent[i, j, k, 17] = 0;

                                                }
                                            }
                                        }
                                    }
                                    SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                }
                                else
                                {
                                    if (j > 0)
                                        if (blockList[i, j - 1, k] == BlockType.None || blockList[i, j - 1, k] == BlockType.Water || blockList[i, j - 1, k] == BlockType.Lava)//still nothing underneath us, but gravity state has just ended
                                        {
                                            BlockType oldblock = blockList[i, j - 1, k];//this replaces any lost water/lava

                                            blockListContent[i, j, k, 11] = 0;
                                            blockListContent[i, j, k, 12] = -100;
                                            blockListContent[i, j, k, 13] = 0;

                                            SetBlock(i, (ushort)(j - 1), k, blockList[i, j, k], blockCreatorTeam[i, j, k]);
                                            for (ushort c = 0; c < 14; c++)//copy content from 0-13
                                            {
                                                blockListContent[i, j - 1, k, c] = blockListContent[i, j, k, c];

                                            }
                                            blockListContent[i, j - 1, k, 10] = frameid;
                                            blockListContent[i, j - 1, k, 14] = (int)(i * 100);
                                            blockListContent[i, j - 1, k, 15] = (int)(j * 100);//120 for curve
                                            blockListContent[i, j - 1, k, 16] = (int)(k * 100);

                                            if (blockListContent[i, j, k, 17] > 0 && blockList[i, j, k] == BlockType.Explosive)//explosive list for tnt update
                                            {
                                                if (blockListContent[i, j, k, 17] == 0)//create owner if we dont have it
                                                {
                                                    foreach (Player p in playerList.Values)
                                                    {
                                                        int cc = p.ExplosiveList.Count;

                                                        int ca = 0;
                                                        while (ca < cc)
                                                        {
                                                            if (p.ExplosiveList[ca].X == i && p.ExplosiveList[ca].Y == j && p.ExplosiveList[ca].Z == k)
                                                            {
                                                                p.ExplosiveList.RemoveAt(ca);
                                                                blockListContent[i, j, k, 17] = (int)(p.ID);
                                                                break;
                                                            }
                                                            ca += 1;
                                                        }
                                                    }
                                                }

                                                if (blockListContent[i, j, k, 17] > 0)
                                                {
                                                    foreach (Player p in playerList.Values)
                                                    {
                                                        if (p.ID == (uint)(blockListContent[i, j, k, 17]))
                                                        {
                                                            //found explosive this belongs to
                                                            p.ExplosiveList.Add(new Vector3(nx, ny, nz));
                                                            blockListContent[nx, ny, nz, 17] = blockListContent[i, j, k, 17];
                                                            p.ExplosiveList.Remove(new Vector3(i, j, k));
                                                            blockListContent[i, j, k, 17] = 0;

                                                        }
                                                    }
                                                }
                                            }
                                            
                                            SetBlock(i, j, k, oldblock, PlayerTeam.None);
                                        }
                                        else
                                        {
                                            PlaySound(InfiniminerSound.RockFall, new Vector3(i, j, k));
                                        }
                                }
                            }
                            else
                            {
                                if (j > 0)//entire section is to allow blocks to drop once they have hit ceiling
                                    if (blockList[i, j - 1, k] == BlockType.None || blockList[i, j - 1, k] == BlockType.Water || blockList[i, j - 1, k] == BlockType.Lava)//still nothing underneath us, but gravity state has just ended
                                    {
                                        BlockType oldblock = blockList[i, j - 1, k];//this replaces any lost water/lava

                                        blockListContent[i, j, k, 11] = 0;
                                        blockListContent[i, j, k, 12] = -100;
                                        blockListContent[i, j, k, 13] = 0;

                                        SetBlock(i, (ushort)(j - 1), k, blockList[i, j, k], blockCreatorTeam[i, j, k]);
                                        for (ushort c = 0; c < 14; c++)//copy content from 0-13
                                        {
                                            blockListContent[i, j - 1, k, c] = blockListContent[i, j, k, c];

                                        }
                                        blockListContent[i, j - 1, k, 10] = frameid;
                                        blockListContent[i, j - 1, k, 14] = (int)(i * 100);
                                        blockListContent[i, j - 1, k, 15] = (int)(j * 100);//120 for curve
                                        blockListContent[i, j - 1, k, 16] = (int)(k * 100);

                                        if (blockListContent[i, j, k, 17] > 0 && blockList[i, j, k] == BlockType.Explosive)//explosive list for tnt update
                                        {
                                            foreach (Player p in playerList.Values)
                                            {
                                                if (p.ID == (uint)(blockListContent[i, j, k, 17]))
                                                {
                                                    //found explosive this belongs to
                                                    p.ExplosiveList.Add(new Vector3(i, j - 1, k));
                                                    blockListContent[i, j - 1, k, 17] = blockListContent[i, j, k, 17];
                                                    p.ExplosiveList.Remove(new Vector3(i, j, k));
                                                    blockListContent[i, j, k, 17] = 0;

                                                }
                                            }
                                        }
                                        SetBlock(i, j, k, oldblock, PlayerTeam.None);
                                    }
                                    else
                                    {
                                        PlaySound(InfiniminerSound.RockFall, new Vector3(i,j,k));
                                    }
                            }

                        }
                        //temperature
                        if (blockList[i, j, k] == BlockType.Lava && blockListContent[i, j, k, 1] > 0)//block is temperature sensitive
                        {
                            //if (blockList[i, j, k] == BlockType.Lava)
                            //{
                            if (blockListContent[i, j, k, 1] > 0)
                            {
                                blockListContent[i, j, k, 1] -= 1;
                                if (blockListContent[i, j, k, 1] == 0)
                                {
                                    SetBlock(i, j, k, BlockType.Mud, PlayerTeam.None);//magma cools down into dirt
                                    blockListContent[i, j, k, 0] = 120;//two minutes of mudout
                                    if (randGen.Next(1, 10) == 5)
                                    {
                                        blockListContent[i, j, k, 1] = (byte)BlockType.Gold;//becomes this block
                                    }
                                    else
                                    {
                                        blockListContent[i, j, k, 1] = (byte)BlockType.Dirt;
                                    }
                                }
                                //    }
                            }
                        }
                            if (blockList[i, j, k] == BlockType.Water && !flowSleep[i, j, k] || blockList[i, j, k] == BlockType.Lava && !flowSleep[i, j, k] || blockList[i, j, k] == BlockType.Fire)//should be liquid check, not comparing each block
                            {//dowaterstuff //dolavastuff

                                BlockType liquid = blockList[i, j, k];
                                BlockType opposing = BlockType.None;

                                BlockType typeBelow = (j <= 0) ? BlockType.Vacuum : blockList[i, j - 1, k];//if j <= 0 then use block vacuum

                                if (liquid == BlockType.Water)
                                {
                                    opposing = BlockType.Lava;
                                }
                                else
                                {
                                    //lava stuff
                                    if (varGetB("roadabsorbs"))
                                    {
                                        BlockType typeAbove = ((int)j == MAPSIZE - 1) ? BlockType.None : blockList[i, j + 1, k];
                                        if (typeAbove == BlockType.Road)
                                        {
                                            SetBlock(i, j, k, BlockType.Road, PlayerTeam.None);
                                        }
                                    }
                                }

                                //if (liquid == BlockType.Lava && blockListContent[i, j, k, 0] > 0)//upcoming volcano
                                //{
                                //    if (i - 1 > 0 && i + 1 < MAPSIZE - 1 && k - 1 > 0 && k + 1 < MAPSIZE - 1 )
                                //    if (blockList[i + 1, j, k] == BlockType.None || blockList[i - 1, j, k] == BlockType.None || blockList[i, j, k + 1] == BlockType.None || blockList[i, j, k - 1] == BlockType.None || blockList[i + 1, j, k] == BlockType.Lava || blockList[i - 1, j, k] == BlockType.Lava || blockList[i, j, k + 1] == BlockType.Lava || blockList[i, j, k - 1] == BlockType.Lava)
                                //    {//if air surrounds the magma, then decrease volcanos power
                                //        blockListContent[i, j, k, 0] = blockListContent[i, j, k, 0] - 1;
                                //        blockListContent[i, j, k, 1] = 240 + blockListContent[i, j, k, 0] * 4;//temperature lowers as volcano gets further from its source
                                //    }

                                //    int x = randGen.Next(-1, 1);
                                //    int z = randGen.Next(-1, 1);

                                //    if (i + x > 0 && i + x < MAPSIZE - 1 && k + z > 0 && k + z < MAPSIZE - 1 && j + 1 < MAPSIZE - 1)
                                //        if (blockList[i + x, j + 1, k + z] != BlockType.Rock)
                                //        {
                                //            SetBlock((ushort)(i + x), (ushort)(j + 1), (ushort)(k + z), liquid, PlayerTeam.None);
                                //            blockListContent[i + x, j + 1, k + z, 0] = blockListContent[i, j, k, 0] - 1;//volcano strength decreases every upblock
                                //            blockListContent[i + x, j + 1, k + z, 1] = randGen.Next(blockListContent[i, j, k, 0]*3, blockListContent[i, j, k, 0]*4);//give it temperature
                                //        }

                                //}

                                if (typeBelow != liquid && varGetB("insane") || liquid == BlockType.Fire)
                                {
                                    if (liquid == BlockType.Fire && blockListContent[i, j, k, 0] == 0)
                                    {
                                    }
                                    else
                                    {
                                        if (i > 0 && blockList[i - 1, j, k] == BlockType.None)
                                        {
                                            SetBlock((ushort)(i - 1), j, k, liquid, PlayerTeam.None);
                                            if (liquid == BlockType.Fire)
                                            {
                                                if (blockListContent[i, j, k, 0] > 5)
                                                    blockListContent[i - 1, j, k, 0] = 1;
                                            }
                                        }
                                        if (k > 0 && blockList[i, j, k - 1] == BlockType.None)
                                        {
                                            SetBlock(i, j, (ushort)(k - 1), liquid, PlayerTeam.None);
                                            if (liquid == BlockType.Fire)
                                            {
                                                if (blockListContent[i, j, k, 0] > 5)
                                                    blockListContent[i, j, k - 1, 0] = 1;
                                            }
                                        }
                                        if ((int)i < MAPSIZE - 1 && blockList[i + 1, j, k] == BlockType.None)
                                        {
                                            SetBlock((ushort)(i + 1), j, k, liquid, PlayerTeam.None);
                                            if (liquid == BlockType.Fire)
                                            {
                                                if (blockListContent[i, j, k, 0] > 5)
                                                    blockListContent[i + 1, j, k, 0] = 1;
                                            }
                                        }
                                        if ((int)k < MAPSIZE - 1 && blockList[i, j, k + 1] == BlockType.None)
                                        {
                                            SetBlock(i, j, (ushort)(k + 1), liquid, PlayerTeam.None);
                                            if (liquid == BlockType.Fire)
                                            {
                                                if (blockListContent[i, j, k, 0] > 5)
                                                    blockListContent[i, j, k + 1, 0] = 1;
                                            }
                                        }
                                    }

                                    if (liquid == BlockType.Fire && blockListContent[i, j, k, 0] > 0)//flame explosion
                                    {
                                        blockListContent[i, j, k, 0] = blockListContent[i, j, k, 0] - 1;
                                        if ((int)j < MAPSIZE - 1 && blockList[i, j + 1, k] == BlockType.None)
                                        {
                                            SetBlock(i, (ushort)(j + 1), k, liquid, PlayerTeam.None);
                                            blockListContent[i, j + 1, k, 0] = blockListContent[i, j, k, 0] - 1;//strength decreases every upblock
                                        }
                                    }
                                    else if (liquid == BlockType.Fire)
                                    {
                                        SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                    }
                                }

                                //check for conflicting lava//may need to check bounds
                                if (opposing != BlockType.None)
                                {
                                    BlockType transform = BlockType.Rock;

                                    if (i > 0 && blockList[i - 1, j, k] == opposing)
                                    {
                                        SetBlock((ushort)(i - 1), j, k, transform, PlayerTeam.None);
                                        //steam
                                    }
                                    if ((int)i < MAPSIZE - 1 && blockList[i + 1, j, k] == opposing)
                                    {
                                        SetBlock((ushort)(i + 1), j, k, transform, PlayerTeam.None);
                                    }
                                    if (j > 0 && blockList[i, j - 1, k] == opposing)
                                    {
                                        SetBlock(i, (ushort)(j - 1), k, transform, PlayerTeam.None);
                                    }
                                    if (j < MAPSIZE - 1 && blockList[i, (ushort)(j + 1), k] == opposing)
                                    {
                                        SetBlock(i, (ushort)(j + 1), k, transform, PlayerTeam.None);
                                    }
                                    if (k > 0 && blockList[i, j, k - 1] == opposing)
                                    {
                                        SetBlock(i, j, (ushort)(k - 1), transform, PlayerTeam.None);
                                    }
                                    if (k < MAPSIZE - 1 && blockList[i, j, k + 1] == opposing)
                                    {
                                        SetBlock(i, j, (ushort)(k + 1), transform, PlayerTeam.None);
                                    }

                                    if (liquid == BlockType.Water)//make mud
                                    {
                                        if (typeBelow == BlockType.Dirt || typeBelow == BlockType.Grass)
                                        {

                                            SetBlock(i, (ushort)(j - 1), k, BlockType.Mud, PlayerTeam.None);
                                            blockListContent[i, j - 1, k, 0] = 120;//two minutes @ 250ms 
                                            blockListContent[i, j - 1, k, 1] = (byte)BlockType.Dirt;//becomes this
                                        }
                                    }
                                }//actual water/liquid calculations
                                if (typeBelow != BlockType.None && typeBelow != liquid)//none//trying radius fill
                                {
                                    for (ushort a = (ushort)(i - 1); a < i + 2; a++)
                                    {
                                        for (ushort b = (ushort)(k - 1); b < k + 2; b++)
                                        {
                                            if (a == (ushort)(i - 1) && b == (ushort)(k - 1))
                                            {
                                                continue;
                                            }
                                            else if (a == i + 1 && b == k + 1)
                                            {
                                                continue;
                                            }
                                            else if (a == i - 1 && b == k + 1)
                                            {
                                                continue;
                                            }
                                            else if (a == i + 1 && b == (ushort)(k - 1))
                                            {
                                                continue;
                                            }

                                            if (blockList[i, j, k] != BlockType.None)//has our water block moved on us?
                                            {
                                                //water slides if standing on an edge
                                                if (a > 0 && b > 0 && a < 64 && b < 64 && j - 1 > 0)
                                                    if (blockList[a, j - 1, b] == BlockType.None)
                                                    {
                                                        SetBlock(a, (ushort)(j - 1), b, liquid, PlayerTeam.None);
                                                        blockListContent[a, j - 1, b, 1] = blockListContent[i, j, k, 1];
                                                        SetBlockDebris(i, j, k, BlockType.None, PlayerTeam.None);
                                                        a = 3;
                                                        break;
                                                    }
                                            }
                                        }
                                    }
                                }
                                else if (typeBelow == liquid || typeBelow == BlockType.None)
                                {
                                    ushort maxradius = 1;//1

                                    while (maxradius < 25)//need to exclude old checks and require a* pathing check to source
                                    {
                                        for (ushort a = (ushort)(-maxradius + i); a <= maxradius + i; a++)
                                        {
                                            for (ushort b = (ushort)(-maxradius + k); b <= maxradius + k; b++)
                                            {
                                                if (a > 0 && b > 0 && a < 64 && b < 64 && j - 1 > 0)
                                                    if (blockList[a, j - 1, b] == BlockType.None)
                                                    {
                                                        if (blockTrace(a, (ushort)(j - 1), b, i, (ushort)(j - 1), k, liquid))//needs to be a pathfind
                                                        {

                                                            if (blockListContent[i, j, k, 0] > 0 && liquid == BlockType.Lava)//volcano
                                                            {
                                                                SetBlock(a, (ushort)(j - 1), b, liquid, PlayerTeam.None);
                                                                blockListContent[a, j - 1, b, 1] = 240 + blockListContent[i, j, k, 0] * 4 + randGen.Next(1, 20);//core stream
                                                            }
                                                            else if (blockListContent[i, j, k, 1] > 0)
                                                            {
                                                                SetBlock(a, (ushort)(j - 1), b, liquid, PlayerTeam.None);
                                                                blockListContent[a, j - 1, b, 1] = 240 + randGen.Next(1, 20);// blockListContent[i, j, k, 0] * 20;
                                                                SetBlockDebris(i, j, k, BlockType.None, PlayerTeam.None);//using vacuum blocks temporary refill
                                                            }
                                                            else
                                                            {
                                                                SetBlock(a, (ushort)(j - 1), b, liquid, PlayerTeam.None);
                                                                SetBlockDebris(i, j, k, BlockType.None, PlayerTeam.None);//using vacuum blocks temporary refill
                                                            }
                                                            maxradius = 30;
                                                            a = 65;
                                                            b = 65;
                                                        }
                                                    }
                                            }

                                        }
                                        maxradius += 1;//prevent water spreading too large, this is mainly to stop loop size getting too large
                                    }
                                    if (maxradius != 30)//block could not find a new home
                                    {
                                        flowSleep[i, j, k] = true;
                                        continue;//skip the surround check
                                    }
                                }
                                //extra checks for sleep
                                uint surround = 0;
                                if (blockList[i, j, k] == liquid)
                                {
                                    for (ushort a = (ushort)(-1 + i); a <= 1 + i; a++)
                                    {
                                        for (ushort b = (ushort)(-1 + j); b <= 1 + j; b++)
                                        {
                                            for (ushort c = (ushort)(-1 + k); c <= 1 + k; c++)
                                            {
                                                if (a > 0 && b > 0 && c > 0 && a < 64 && b < 64 && c < 64)
                                                {
                                                    if (blockList[a, b, c] != BlockType.None)
                                                    {
                                                        surround += 1;//block is surrounded by types it cant move through
                                                    }
                                                }
                                                else//surrounded by edge of map
                                                {
                                                    surround += 1;
                                                }
                                            }
                                        }
                                    }
                                    if (surround >= 27)
                                    {
                                        flowSleep[i, j, k] = true;
                                    }
                                }
                            }

                            else if (blockList[i, j, k] == BlockType.Pump && blockListContent[i, j, k, 0] > 0)// content0 = determines if on
                            {//dopumpstuff
                                BlockType pumpheld = BlockType.None;

                                if (i + blockListContent[i, j, k, 2] < MAPSIZE && j + blockListContent[i, j, k, 3] < MAPSIZE && k + blockListContent[i, j, k, 4] < MAPSIZE && i + blockListContent[i, j, k, 2] > 0 && j + blockListContent[i, j, k, 3] > 0 && k + blockListContent[i, j, k, 4] > 0)
                                {
                                    if (blockList[i + blockListContent[i, j, k, 2], j + blockListContent[i, j, k, 3], k + blockListContent[i, j, k, 4]] == BlockType.Water)
                                    {
                                        pumpheld = BlockType.Water;
                                        SetBlock((ushort)(i + blockListContent[i, j, k, 2]), (ushort)(j + blockListContent[i, j, k, 3]), (ushort)(k + blockListContent[i, j, k, 4]), BlockType.None, PlayerTeam.None);
                                    }
                                    if (blockList[i + blockListContent[i, j, k, 2], j + blockListContent[i, j, k, 3], k + blockListContent[i, j, k, 4]] == BlockType.Lava)
                                    {
                                        pumpheld = BlockType.Lava;
                                        SetBlock((ushort)(i + blockListContent[i, j, k, 2]), (ushort)(j + blockListContent[i, j, k, 3]), (ushort)(k + blockListContent[i, j, k, 4]), BlockType.None, PlayerTeam.None);
                                    }

                                    if (pumpheld != BlockType.None)
                                    {
                                        if (i + blockListContent[i, j, k, 5] < MAPSIZE && j + blockListContent[i, j, k, 6] < MAPSIZE && k + blockListContent[i, j, k, 7] < MAPSIZE && i + blockListContent[i, j, k, 5] > 0 && j + blockListContent[i, j, k, 6] > 0 && k + blockListContent[i, j, k, 7] > 0)
                                        {
                                            if (blockList[i + blockListContent[i, j, k, 5], j + blockListContent[i, j, k, 6], k + blockListContent[i, j, k, 7]] == BlockType.None)
                                            {//check bounds
                                                SetBlock((ushort)(i + blockListContent[i, j, k, 5]), (ushort)(j + blockListContent[i, j, k, 6]), (ushort)(k + blockListContent[i, j, k, 7]), pumpheld, PlayerTeam.None);//places its contents in desired direction
                                            }
                                            else if (blockList[i + blockListContent[i, j, k, 5], j + blockListContent[i, j, k, 6], k + blockListContent[i, j, k, 7]] == pumpheld)//exit must be clear or same substance
                                            {
                                                for (ushort m = 2; m < 10; m++)//multiply exit area to fake upward/sideward motion
                                                {
                                                    if (i + blockListContent[i, j, k, 5] * m < MAPSIZE && j + blockListContent[i, j, k, 6] * m < MAPSIZE && k + blockListContent[i, j, k, 7] * m < MAPSIZE && i + blockListContent[i, j, k, 5] * m > 0 && j + blockListContent[i, j, k, 6] * m > 0 && k + blockListContent[i, j, k, 7] * m > 0)
                                                    {
                                                        if (blockList[i + blockListContent[i, j, k, 5] * m, j + blockListContent[i, j, k, 6] * m, k + blockListContent[i, j, k, 7] * m] == BlockType.None)
                                                        {
                                                            SetBlock((ushort)(i + blockListContent[i, j, k, 5] * m), (ushort)(j + blockListContent[i, j, k, 6] * m), (ushort)(k + blockListContent[i, j, k, 7] * m), pumpheld, PlayerTeam.None);//places its contents in desired direction at a distance
                                                            break;//done with this pump
                                                        }
                                                        else// if (blockList[i + blockListContent[i, j, k, 5] * m, j + blockListContent[i, j, k, 6] * m, k + blockListContent[i, j, k, 7] * m] != pumpheld)//check that we're not going through walls to pump this
                                                        {
                                                            break;//pipe has run aground .. and dont refund the intake
                                                        }
                                                    }

                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            else if (blockList[i, j, k] == BlockType.Pipe) // Do pipe stuff
                            {
                                // Check if pipe connected to a source

                                int PipesConnected = 0;
                                int BlockIsSource = 0;
                                BlockType PipeSourceLiquid = BlockType.None;

                                for (ushort a = (ushort)(-1 + i); a < 2 + i; a++)
                                {
                                    for (ushort b = (ushort)(-1 + j); b < 2 + j; b++)
                                    {
                                        for (ushort c = (ushort)(-1 + k); c < 2 + k; c++)
                                        {
                                            if (a > 0 && b > 0 && c > 0 && a < 64 && b < 64 && c < 64)
                                            {
                                                if (a == i && b == j && c == k)
                                                {
                                                    continue;
                                                }
                                                else
                                                {
                                                    if (blockList[a, b, c] == BlockType.Water || blockList[a, b, c] == BlockType.Lava)//we are either the dst or src
                                                    {
                                                        //PipeSourceLiquid = blockList[a, b, c];
                                                        //blockListContent[i, j, k, 1] = 1; // Set as connected
                                                        //ChainConnectedToSource = 1;
                                                        if (blockListContent[i, j, k, 4] != 1 && blockListContent[i, j, k, 3] == 1)//too early to have full connection count here
                                                        {
                                                            BlockIsSource = 1;
                                                            //blockListContent[i, j, k, 2] = 1;// Set as a source pipe

                                                            //blockListContent[i, j, k, 5] = i;
                                                            //blockListContent[i, j, k, 6] = j;
                                                            //blockListContent[i, j, k, 7] = k;//src happens to know itself to spread the love
                                                            //SetBlock(a, b, c, BlockType.None, PlayerTeam.None);
                                                            //blockListContent[i, j, k, 9] = (byte)(blockList[a, b, c]);
                                                            //blockListContent[i, j, k, 8] += 1;//liquidin
                                                            // blockListContent[i, j, k, 8] = 0;//pipe starts with no liquid
                                                        }
                                                    }

                                                    if (blockList[a, b, c] == BlockType.Pipe)//Found a pipe surrounding this pipe
                                                    {
                                                        if ((a == (ushort)(i + 1) || a == (ushort)(i - 1) || a == (ushort)(i)) && b != j && c != k)
                                                        {
                                                            continue;
                                                        }
                                                        else if (a != i && (b == (ushort)(j + 1) || b == (ushort)(j - 1) || b == (ushort)(j)) && c != k)
                                                        {
                                                            continue;
                                                        }
                                                        else if (a != i && b != j && (c == (ushort)(k + 1) || c == (ushort)(k - 1) || c == (ushort)(k)))
                                                       {
                                                            continue;
                                                        }
                                                        if (blockList[a, b, c] == BlockType.Pipe)//Found a pipe surrounding this pipe
                                                        {
                                                            if (blockListContent[a, b, c, 1] == 1 && (a == i || b == j || c == k))//Check if other pipe connected to a source
                                                            {
                                                                //ChainConnectedToSource = 1;
                                                                blockListContent[i, j, k, 1] = 1; //set as connected chain connected to source
                                                            }
                                                            if (blockListContent[a, b, c, 5] > 0)// && blockListContent[i, j, k, 5] == 0)//this pipe knows the source! hook us up man.
                                                            {
                                                                blockListContent[i, j, k, 5] = blockListContent[a, b, c, 5];//record src 
                                                                blockListContent[i, j, k, 6] = blockListContent[a, b, c, 6];
                                                                blockListContent[i, j, k, 7] = blockListContent[a, b, c, 7];
                                                                // ConsoleWrite("i" + i + "j" + j + "k" + k + " got src: " + blockListContent[a, b, c, 5] + "/" + blockListContent[a, b, c, 6] + "/" + blockListContent[a, b, c, 7]);
                                                            }
                                                            if (blockListContent[i, j, k, 5] > 0)
                                                            {
                                                                if (blockListContent[blockListContent[i, j, k, 5], blockListContent[i, j, k, 6], blockListContent[i, j, k, 7], 3] != 1)
                                                                {//src no longer valid
                                                                    blockListContent[i, j, k, 5] = 0;
                                                                    ConsoleWrite("src negated");
                                                                }
                                                            }

                                                            PipesConnected += 1;
                                                            blockListContent[i, j, k, 3] = PipesConnected;// Set number of pipes connected to pipe
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                if (BlockIsSource == 1 && blockListContent[i, j, k, 3] == 1)
                                {
                                    blockListContent[i, j, k, 2] = 1;// Set as a source pipe

                                    blockListContent[i, j, k, 5] = i;
                                    blockListContent[i, j, k, 6] = j;
                                    blockListContent[i, j, k, 7] = k;//src happens to know itself to spread the love

                                    for (ushort a2 = (ushort)(-1 + i); a2 < 2 + i; a2++)
                                    {
                                        for (ushort b2 = (ushort)(-1 + j); b2 < 2 + j; b2++)
                                        {
                                            for (ushort c2 = (ushort)(-1 + k); c2 < 2 + k; c2++)
                                            {
                                                if (a2 > 0 && b2 > 0 && c2 > 0 && a2 < 64 && b2 < 64 && c2 < 64)
                                                {
                                                    if (blockList[a2, b2, c2] == BlockType.Water || blockList[a2, b2, c2] == BlockType.Lava)
                                                    {
                                                        PipeSourceLiquid = blockList[a2, b2, c2];
                                                        blockListContent[i, j, k, 1] = 1;
                                                        blockListContent[i, j, k, 5] = i;
                                                        blockListContent[i, j, k, 6] = j;
                                                        blockListContent[i, j, k, 7] = k;//src happens to know itself to spread the love
                                                        SetBlock(a2, b2, c2, BlockType.None, PlayerTeam.None);
                                                        blockListContent[i, j, k, 9] = (byte)(blockList[a2, b2, c2]);
                                                        blockListContent[i, j, k, 8] += 1;//liquidin
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                if (blockListContent[i, j, k, 3] > 1)
                                {
                                    blockListContent[i, j, k, 2] = 0;// do notSet as a source pipe
                                }

                                if (blockListContent[i, j, k, 1] == 1 && blockListContent[i, j, k, 3] == 1 && blockListContent[i, j, k, 2] == 0)
                                {
                                    blockListContent[i, j, k, 4] = 1; //Set as a Destination Pipe
                                    if(blockListContent[i,j,k,5] > 0)//do we know where the src is?
                                    if (blockListContent[blockListContent[i, j, k, 5], blockListContent[i, j, k, 6], blockListContent[i, j, k, 7], 2] == 1 && blockList[blockListContent[i, j, k, 5], blockListContent[i, j, k, 6], blockListContent[i, j, k, 7]] == BlockType.Pipe)
                                    for (ushort bob = (ushort)(-1 + i); bob < 2 + i; bob++)
                                    {
                                        for (ushort fat = (ushort)(-1 + k); fat < 2 + k; fat++)
                                        {
                                            if (blockList[bob, j + 1, fat] == BlockType.None)
                                            {
                                                if (blockListContent[blockListContent[i, j, k, 5], blockListContent[i, j, k, 6], blockListContent[i, j, k, 7], 8] > 0)
                                                {
                                                    //blockList[bob, j + 1, fat] = PipeSourceLiquid;
                                                    SetBlock(bob, (ushort)(j + 1), fat, BlockType.Water, PlayerTeam.None);// (BlockType)(blockListContent[blockListContent[i, j, k, 5], blockListContent[i, j, k, 6], blockListContent[i, j, k, 7], 9]), PlayerTeam.None);
                                                    ConsoleWrite("pump attempt");
                                                    blockListContent[blockListContent[i, j, k, 5], blockListContent[i, j, k, 6], blockListContent[i, j, k, 7], 8] -= 1;
                                                }
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    blockListContent[i, j, k, 4] = 0;
                                }



                                /*
                                if (ChainConnectedToSource == 0 && PipeIsSource == 0)
                                {
                                    blockListContent[i, j, k, 1] = 0;
                                    blockListContent[i, j, k, 2] = 0;
                                }
                                if (PipeIsSource == 0)
                                {
                                    blockListContent[i, j, k, 2] = 0;
                                }

                                if (blockListContent[i, j, k, 3] == 1 && blockListContent[i, j, k, 2] == 0 && blockListContent[i, j, k, 1] == 1)// find outputs (not source with 1 pipe only connected)
                                {
                                    //set as dst pipe
                                    blockListContent[i, j, k, 4] = 1;
                                }
                                else
                                {
                                    blockListContent[i, j, k, 4] = 0;
                                }

                                if (blockListContent[i, j, k, 4] == 1)
                                {
                                    if (blockList[i , j + 1, k] == BlockType.None) 
                                    {
                                        blockList[i, j + 1, k] = BlockType.Water;
                                    }

                                }
                                */
                            }
                            else if (blockList[i, j, k] == BlockType.Barrel)
                            {//docompressorstuff

                                if (blockListContent[i, j, k, 0] == 1)
                                {
                                    if (blockListContent[i, j, k, 2] < 20)//not full
                                    for (ushort a = (ushort)(-1 + i); a < 2 + i; a++)
                                    {
                                        for (ushort b = (ushort)(-1 + j); b < 2 + j; b++)
                                        {
                                            for (ushort c = (ushort)(-1 + k); c < 2 + k; c++)
                                            {
                                                if (a > 0 && b > 0 && c > 0 && a < 64 && b < 64 && c < 64)
                                                {
                                                    if (blockList[a, b, c] == BlockType.Water || blockList[a, b, c] == BlockType.Lava)
                                                    {
                                                        if (blockListContent[i, j, k, 1] == 0 || blockListContent[i, j, k, 2] == 0)
                                                        {
                                                            blockListContent[i, j, k, 1] = (byte)blockList[a, b, c];
                                                            SetBlock((ushort)(a), (ushort)(b), (ushort)(c), BlockType.None, PlayerTeam.None);
                                                            blockListContent[i, j, k, 2] += 1;
                                                        }
                                                        else if (blockListContent[i, j, k, 1] == (byte)blockList[a, b, c])
                                                        {
                                                            SetBlock((ushort)(a), (ushort)(b), (ushort)(c), BlockType.None, PlayerTeam.None);
                                                            blockListContent[i, j, k, 2] += 1;
                                                        }
                                                    }

                                                }

                                            }
                                        }
                                    }
                                }
                                else//venting
                                {
                                    if (blockListContent[i, j, k, 1] > 0)//has type
                                    {
                                        if (blockListContent[i, j, k, 2] > 0)//has content
                                        {
                                            if (blockList[i, j + 1, k] == BlockType.None)
                                            {
                                                SetBlock(i, (ushort)(j + 1), k, (BlockType)(blockListContent[i, j, k, 1]), PlayerTeam.None);//places its contents in desired direction at a distance
                                                blockListContent[i, (ushort)(j + 1), k, 1] = 120;
                                                blockListContent[i, j, k, 2] -= 1;
                                                continue;
                                            }
                                            else if (blockList[i, j + 1, k] == (BlockType)(blockListContent[i, j, k, 1]))//exit must be clear or same substance
                                            {
                                                blockListContent[i, j + 1, k, 1] = 120;//refresh temperature
                                                for (ushort m = 2; m < 10; m++)//multiply exit area to fake upward motion
                                                {
                                                    if (j + m < MAPSIZE)
                                                    {
                                                        if (blockList[i, j + m, k] == BlockType.None)
                                                        {
                                                            SetBlock(i, (ushort)(j + m), k, (BlockType)(blockListContent[i, j, k, 1]), PlayerTeam.None);//places its contents in desired direction at a distance
                                                            blockListContent[i, (ushort)(j + m), k, 1] = 120;
                                                            blockListContent[i, j, k, 2] -= 1;
                                                            break;//done with this pump
                                                        }
                                                        else if (blockList[i, j + m, k] != (BlockType)(blockListContent[i, j, k, 1]))// if (blockList[i + blockListContent[i, j, k, 5] * m, j + blockListContent[i, j, k, 6] * m, k + blockListContent[i, j, k, 7] * m] != pumpheld)//check that we're not going through walls to pump this
                                                        {
                                                            break;//pipe has run aground .. and dont refund the intake
                                                        }
                                                        else//must be the liquid in the way, refresh its temperature
                                                        {
                                                            blockListContent[i, j + m, k, 1] = 120;
                                                        }
                                                    }

                                                }
                                            }
                                        }
                                    }
                                    else//had type in contents but no content
                                    {
                                        blockListContent[i, j, k, 1] = 0;
                                    }
                                }

                                if (j + 1 < MAPSIZE && j - 1 > 0 && i - 1 > 0 && i + 1 < MAPSIZE && k - 1 > 0 && k + 1 < MAPSIZE)
                                    if (blockList[i, j - 1, k] == BlockType.None)
                                    {//no block above or below, so fall
                                        blockListContent[i, j, k, 10] = frameid;
                                        blockListContent[i, j, k, 11] = 0;
                                        blockListContent[i, j, k, 12] = -100;
                                        blockListContent[i, j, k, 13] = 0;
                                        blockListContent[i, j, k, 14] = i * 100;
                                        blockListContent[i, j, k, 15] = j * 100;
                                        blockListContent[i, j, k, 16] = k * 100;
                                        blockListContent[i, j, k, 0] = 0;//empty
                                        continue;
                                    }
                            }
                            else if (blockList[i, j, k] == BlockType.Spring)
                            {//dospringstuff
                                if (blockList[i, j - 1, k] == BlockType.None)
                                {
                                    SetBlock(i, (ushort)(j - 1), k, BlockType.Water, PlayerTeam.None);
                                }
                            }
                            else if (blockList[i, j, k] == BlockType.MagmaVent)
                            {//dospringstuff
                                if (blockList[i, j - 1, k] == BlockType.None)
                                {
                                    SetBlock(i, (ushort)(j - 1), k, BlockType.Lava, PlayerTeam.None);
                                }
                            }
                            else if (blockList[i, j, k] == BlockType.MagmaBurst)
                            {//dospringstuff
                                if (blockListContent[i, j, k, 0] < 10 && blockListContent[i, j, k, 1] > 0)
                                {
                                    blockListContent[i, j, k, 0]++;

                                    if (j > 0)
                                            for (ushort m = 1; m < 10; m++)//multiply exit area
                                            {
                                                if (j - m > 0)
                                                {
                                                    if (blockList[i, j - m, k] == BlockType.None)
                                                    {
                                                        SetBlock(i, (ushort)(j - m), k, BlockType.Lava, PlayerTeam.None);//places its contents in desired direction at a distance
                                                        blockListContent[i, j - m, k, 1] = 40;
                                                        break;
                                                    }
                                                    else if (blockList[i, j - m, k] == BlockType.Lava)
                                                    {
                                                        continue;
                                                    }
                                                    else
                                                    {
                                                        break;
                                                    }
                                                }
                                                else
                                                {
                                                    break;
                                                }
                                            }

                                    if (j < MAPSIZE - 1)
                                        for (ushort m = 1; m < 10; m++)//multiply exit area
                                        {
                                            if (j + m < MAPSIZE - 1)
                                            {
                                                if (blockList[i, j + m, k] == BlockType.None)
                                                {
                                                    SetBlock(i, (ushort)(j + m), k, BlockType.Lava, PlayerTeam.None);//places its contents in desired direction at a distance
                                                    blockListContent[i, j + m, k, 1] = 40;
                                                    break;
                                                }
                                                else if (blockList[i, j + m, k] == BlockType.Lava)
                                                {
                                                    continue;
                                                }
                                                else
                                                {
                                                    break;
                                                }
                                            }
                                            else
                                            {
                                                break;
                                            }

                                        }

                                    if (i > 0)
                                            for (ushort m = 1; m < 10; m++)//multiply exit area
                                            {
                                                if (i - m > 0)
                                                {
                                                    if (blockList[i - m, j, k] == BlockType.None)
                                                    {
                                                        SetBlock((ushort)(i - m), j, k, BlockType.Lava, PlayerTeam.None);//places its contents in desired direction at a distance
                                                        blockListContent[i - m, j, k, 1] = 40;
                                                        break;
                                                    }
                                                    else if (blockList[i - m, j, k] == BlockType.Lava)
                                                    {
                                                        continue;
                                                    }
                                                    else
                                                    {
                                                        break;
                                                    }
                                                }
                                                else
                                                {
                                                    break;
                                                }

                                            }

                                    if (i < MAPSIZE - 1)
                                            for (ushort m = 1; m < 10; m++)//multiply exit area
                                            {
                                                if (i + m < MAPSIZE - 1)
                                                {
                                                    if (blockList[i + m, j, k] == BlockType.None)
                                                    {
                                                        SetBlock((ushort)(i + m), j, k, BlockType.Lava, PlayerTeam.None);//places its contents in desired direction at a distance
                                                        blockListContent[i + m, j, k, 1] = 40;
                                                        break;
                                                    }
                                                    else if (blockList[i + m, j, k] == BlockType.Lava)
                                                    {
                                                        continue;
                                                    }
                                                    else
                                                    {
                                                        break;
                                                    }
                                                }
                                                else
                                                {
                                                    break;
                                                }

                                            }

                                    if (k > 0)
                                            for (ushort m = 1; m < 10; m++)//multiply exit area
                                            {
                                                if (k - m > 0)
                                                {
                                                    if (blockList[i, j, k - m] == BlockType.None)
                                                    {
                                                        SetBlock(i, j, (ushort)(k - m), BlockType.Lava, PlayerTeam.None);//places its contents in desired direction at a distance
                                                        blockListContent[i, j, k - m, 1] = 40;
                                                    }
                                                    else if (blockList[i, j, k - m] == BlockType.Lava)
                                                    {
                                                        continue;
                                                    }
                                                    else
                                                    {
                                                        break;
                                                    }
                                                }
                                                else
                                                {
                                                    break;
                                                }
                                            }

                                    if (k < MAPSIZE - 1)
                                            for (ushort m = 1; m < 10; m++)//multiply exit area
                                            {
                                                if (k + m < MAPSIZE - 1)
                                                {
                                                    if (blockList[i, j, k + m] == BlockType.None)
                                                    {
                                                        SetBlock(i, j, (ushort)(k + m), BlockType.Lava, PlayerTeam.None);//places its contents in desired direction at a distance
                                                        blockListContent[i, j, k + m, 1] = 40;
                                                    }
                                                    else if (blockList[i, j, k + m] == BlockType.Lava)
                                                    {
                                                        continue;
                                                    }
                                                    else
                                                    {
                                                        break;
                                                    }
                                                }
                                                else
                                                {
                                                    break;
                                                }

                                            }
                                }
                                else if (blockListContent[i, j, k, 1] < 1)//priming time / 400ms
                                {
                                    if (j > 0)
                                        if (blockList[i, j - 1, k] == BlockType.None)
                                        {
                                            blockListContent[i, j, k, 1]++;
                                        }

                                    if (j < MAPSIZE - 1)
                                        if (blockList[i, j + 1, k] == BlockType.None)
                                        {
                                            blockListContent[i, j, k, 1]++;
                                        }

                                    if (i > 0)
                                        if (blockList[i - 1, j, k] == BlockType.None)
                                        {
                                            blockListContent[i, j, k, 1]++;
                                        }

                                    if (i < MAPSIZE - 1)
                                        if (blockList[i + 1, j, k] == BlockType.None)
                                        {
                                            blockListContent[i, j, k, 1]++;
                                        }

                                    if (k > 0)
                                        if (blockList[i, j, k - 1] == BlockType.None)
                                        {
                                            blockListContent[i, j, k, 1]++;
                                        }

                                    if (k < MAPSIZE - 1)
                                        if (blockList[i, j, k + 1] == BlockType.None)
                                        {
                                            blockListContent[i, j, k, 1]++;
                                        }

                                    if (blockListContent[i, j, k, 1] == 0)
                                    {
                                        //talk a walk around the map
                                        if (randGen.Next(1000) == 1 && sleeping == false)//500+
                                        {
                                            int x = i + randGen.Next(2) - 1;
                                            int y = j + randGen.Next(2) - 1;
                                            int z = k + randGen.Next(2) - 1;

                                            if (x < 1 || y < 1 || z < 1 || x > MAPSIZE - 2 || y > MAPSIZE - 2 || z > MAPSIZE - 2)
                                            {
                                            }
                                            else
                                            {
                                                if (blockList[x - 1, y, z] != BlockType.None)
                                                    if (blockList[x + 1, y, z] != BlockType.None)
                                                        if (blockList[x, y - 1, z] != BlockType.None)
                                                            if (blockList[x, y + 1, z] != BlockType.None)
                                                                if (blockList[x, y, z - 1] != BlockType.None)
                                                                    if (blockList[x, y, z + 1] != BlockType.None)
                                                                    {
                                                                     //   ConsoleWrite("magmaburst moved from " + i + "/" + j + "/" + k + " to " + x + "/" + y + "/" + z);
                                                                        SetBlock((ushort)i, (ushort)j, (ushort)k, BlockType.Dirt, PlayerTeam.None);
                                                                        SetBlock((ushort)x, (ushort)y, (ushort)z, BlockType.MagmaBurst, PlayerTeam.None);
                                                                    }
                                            }
                                        }
                                    }
                                }
                                else//run out of magma->turn into rock (gold became too frequent)
                                {
                                    SetBlock(i, j, k, BlockType.Rock, PlayerTeam.None);
                                    blockListHP[i, j, k] = BlockInformation.GetHP(BlockType.Rock);
                                }
                            }
                            else if (blockList[i, j, k] == BlockType.Mud)//mud dries out
                            {
                                if (blockList[i, j - 1, k] != BlockType.Water)
                                    if (blockListContent[i, j, k, 0] < 1)
                                    {
                                        blockListContent[i, j, k, 0] = 0;
                                        SetBlock(i, j, k, (BlockType)blockListContent[i, j, k, 1], PlayerTeam.None);
                                        blockListHP[i,j,k] = BlockInformation.GetHP((BlockType)blockListContent[i, j, k, 1]);
                                    }
                                    else
                                    {
                                        blockListContent[i, j, k, 0] -= 1;
                                    }
                            }
                            else if (blockList[i, j, k] == BlockType.Sand)//sand falls straight down and moves over edges
                            {
                                if (j - 1 > 0)
                                {
                                    if (blockList[i, j - 1, k] == BlockType.None && blockListContent[i, j, k, 10] == 0)
                                    {
                                        blockListContent[i, j, k, 10] = frameid;
                                        blockListContent[i, j, k, 11] = 0;
                                        blockListContent[i, j, k, 12] = -100;
                                        blockListContent[i, j, k, 13] = 0;
                                        blockListContent[i, j, k, 14] = i*100;
                                        blockListContent[i, j, k, 15] = j*100;
                                        blockListContent[i, j, k, 16] = k*100;
                                        //SetBlock(i, (ushort)(j - 1), k, BlockType.Sand, PlayerTeam.None);
                                        //SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                        continue;
                                    }

                                    if (j - 2 > 0)
                                    if (blockList[i, j - 1, k] == BlockType.Sand && blockListContent[i, j, k, 10] == 0)
                                    for (ushort m = 1; m < 2; m++)//how many squares to fall over
                                    {
                                        if (i + m < MAPSIZE)
                                            if (blockList[i + m, j - 1, k] == BlockType.None)
                                            {
                                                SetBlock((ushort)(i + m), (ushort)(j - 1), k, BlockType.Sand, PlayerTeam.None);
                                                SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                                continue;
                                            }
                                        if (i - m > 0)
                                            if (blockList[i - m, j - 1, k] == BlockType.None)
                                            {
                                                SetBlock((ushort)(i - m), (ushort)(j - 1), k, BlockType.Sand, PlayerTeam.None);
                                                SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                                continue;
                                            }
                                        if (k + m < MAPSIZE)
                                            if (blockList[i, j - 1, k + m] == BlockType.None)
                                            {
                                                SetBlock(i, (ushort)(j - 1), (ushort)(k + m), BlockType.Sand, PlayerTeam.None);
                                                SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                                continue;
                                            }
                                        if (k - m > 0)
                                            if (blockList[i, j - 1, k - m] == BlockType.None)
                                            {
                                                SetBlock(i, (ushort)(j - 1), (ushort)(k - m), BlockType.Sand, PlayerTeam.None);
                                                SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                                continue;
                                            }
                                    }
                                }
                            }
                            else if (blockList[i, j, k] == BlockType.Dirt || blockList[i, j, k] == BlockType.Grass)//loose dirt falls straight down / topmost dirt grows
                            {
                                if (j + 1 < MAPSIZE && j - 1 > 0 && i - 1 > 0 && i + 1 < MAPSIZE && k - 1 > 0 && k + 1 < MAPSIZE)
                                {
                                    if (blockListContent[i, j, k, 0] > 0)
                                    {
                                            blockListContent[i, j, k, 0]--;
                                            //greenery
                                            if (blockListContent[i, j, k, 0] > 150 && blockList[i, j, k] == BlockType.Dirt)
                                            {
                                                SetBlock(i, j, k, BlockType.Grass, PlayerTeam.None);
                                                blockListContent[i, j, k, 0] = 150;
                                            }
                                            else if (blockListContent[i, j, k, 0] == 0 && blockList[i, j, k] == BlockType.Grass)
                                            {
                                                SetBlock(i, j, k, BlockType.Dirt, PlayerTeam.None);
                                            }
                                    }

                                    if (blockList[i, j - 1, k] == BlockType.None)
                                        if (blockList[i, j + 1, k] == BlockType.None && blockList[i + 1, j, k] == BlockType.None && blockList[i - 1, j, k] == BlockType.None && blockList[i, j, k + 1] == BlockType.None && blockList[i, j, k - 1] == BlockType.None && blockListContent[i, j, k, 10] == 0)
                                        {//no block above or below, so fall
                                            blockListContent[i, j, k, 10] = frameid;
                                            blockListContent[i, j, k, 11] = 0;
                                            blockListContent[i, j, k, 12] = -100;
                                            blockListContent[i, j, k, 13] = 0;
                                            blockListContent[i, j, k, 14] = i * 100;
                                            blockListContent[i, j, k, 15] = j * 100;
                                            blockListContent[i, j, k, 16] = k * 100;
                                            // SetBlock(i, (ushort)(j - 1), k, BlockType.Dirt, PlayerTeam.None);
                                            //SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                            continue;
                                        }
                                }
                               
                            }
                            else if (blockList[i, j, k] == BlockType.RadarRed)
                            {
                                blockListContent[i, j, k, 0] += 1;

                                if (blockListContent[i, j, k, 0] == 2)//limit scans
                                {
                                    blockListContent[i, j, k, 0] = 0;
                                    foreach (Player p in playerList.Values)
                                    {
                                        if (p.Alive && p.Team == PlayerTeam.Blue)
                                            if (Get3DDistance((int)(p.Position.X), (int)(p.Position.Y), (int)(p.Position.Z), i, j, k) < 20)
                                            {
                                                //this player has been detected by the radar
                                                //should check if stealthed
                                                if (p.Content[1] == 0)
                                                {
                                                    if (p.Class == PlayerClass.Prospector && p.Content[5] == 1)
                                                    {//character is hidden
                                                    }
                                                    else
                                                    {
                                                        p.Content[1] = 1;//goes on radar
                                                        SendPlayerContentUpdate(p, 1);
                                                    }
                                                }
                                            }
                                            else//player is out of range
                                            {
                                                if (p.Content[1] == 1)
                                                {
                                                    p.Content[1] = 0;//goes off radar again
                                                    SendPlayerContentUpdate(p, 1);
                                                }
                                            }
                                    }
                                }
                            }
                            else if (blockList[i, j, k] == BlockType.RadarBlue)
                            {
                                blockListContent[i, j, k, 0] += 1;

                                if (blockListContent[i, j, k, 0] == 2)//limit scans
                                {
                                    blockListContent[i, j, k, 0] = 0;
                                    foreach (Player p in playerList.Values)
                                    {
                                        if (p.Alive && p.Team == PlayerTeam.Red)
                                            if (Get3DDistance((int)(p.Position.X), (int)(p.Position.Y), (int)(p.Position.Z), i, j, k) < 20)
                                            {
                                                //this player has been detected by the radar
                                                //should check if stealthed
                                                if (p.Content[1] == 0)
                                                {
                                                    if (p.Class == PlayerClass.Prospector && p.Content[5] == 1)
                                                    {//character is hidden
                                                    }
                                                    else
                                                    {
                                                        p.Content[1] = 1;//goes on radar
                                                        SendPlayerContentUpdate(p, 1);
                                                    }
                                                }
                                            }
                                            else//player is out of range
                                            {
                                                if (p.Content[1] == 1)
                                                {
                                                    p.Content[1] = 0;//goes off radar again
                                                    SendPlayerContentUpdate(p, 1);
                                                }
                                            }
                                    }
                                }
                            }
                            else if (blockList[i, j, k] == BlockType.Plate && blockListContent[i, j, k, 1] > 0)
                            {
                                if (blockListContent[i, j, k, 1] == 1)
                                {
                                    blockListContent[i, j, k, 1] = 0;
                                    //untrigger the plate
                                    Trigger(i, j, k, i, j, k, 1, null);
                                }
                                else
                                {
                                    blockListContent[i, j, k, 1] -= 1;
                                }

                            }
                            else if (blockList[i, j, k] == BlockType.ResearchB && blockListContent[i, j, k, 0] > 0)
                            {
                                if (blockListContent[i, j, k, 1] > 0)
                                {
                                    if (teamCashBlue > 0 && ResearchProgress[(byte)PlayerTeam.Blue, blockListContent[i, j, k, 1]] > 0 && blockListContent[i, j, k, 4] == 0)
                                    {
                                        ResearchProgress[(byte)PlayerTeam.Blue, blockListContent[i, j, k, 1]]--;

                                        blockListContent[i, j, k, 4] = 3;//timer
                                        blockListContent[i, j, k, 3] = 0;//message warnings
                                        teamCashBlue -= 1;

                                        NetBuffer msgBuffer = netServer.CreateBuffer();
                                        foreach (NetConnection netConn in playerList.Keys)
                                            if (netConn.Status == NetConnectionStatus.Connected)
                                                SendTeamCashUpdate(playerList[netConn]);

                                        if (ResearchProgress[(byte)PlayerTeam.Blue, blockListContent[i, j, k, 1]] == 0)//research complete
                                        {
                                            ResearchComplete[(byte)PlayerTeam.Blue, blockListContent[i, j, k, 1]]++;//increase rank
                                            ResearchProgress[(byte)PlayerTeam.Blue, blockListContent[i, j, k, 1]] = ResearchInformation.GetCost((Research)blockListContent[i, j, k, 1]) * (ResearchComplete[(byte)PlayerTeam.Blue, blockListContent[i, j, k, 1]] + 1);
                                            NetBuffer msgBufferr = netServer.CreateBuffer();
                                            msgBufferr = netServer.CreateBuffer();
                                            msgBufferr.Write((byte)InfiniminerMessage.ChatMessage);

                                            msgBufferr.Write((byte)ChatMessageType.SayBlueTeam);
                                            if (ResearchComplete[(byte)PlayerTeam.Blue, blockListContent[i, j, k, 1]] == 1)//first rank
                                                msgBufferr.Write(Defines.Sanitize(ResearchInformation.GetName((Research)blockListContent[i, j, k, 1]) + " research was completed!"));
                                            else
                                                msgBufferr.Write(Defines.Sanitize(ResearchInformation.GetName((Research)blockListContent[i, j, k, 1]) + " rank " + ResearchComplete[(byte)PlayerTeam.Blue, blockListContent[i, j, k, 1]] + " research was completed!"));
                                            foreach (NetConnection netConn in playerList.Keys)
                                                if (netConn.Status == NetConnectionStatus.Connected)
                                                    if (playerList[netConn].Team == PlayerTeam.Blue)
                                                        netServer.SendMessage(msgBufferr, netConn, NetChannel.ReliableUnordered); 

                                            //recalculate player statistics/buffs
                                            ResearchRecalculate(PlayerTeam.Blue, blockListContent[i, j, k, 1]);
                                            //
                                            blockListContent[i, j, k, 1] = 0;
                                            blockListContent[i, j, k, 0] = 0;
                                        }
                                    }
                                    else if (blockListContent[i, j, k, 4] > 0)
                                    {
                                        blockListContent[i, j, k, 4]--;
                                    }
                                    else
                                    {
                                        if (blockListContent[i, j, k, 3] == 0)
                                        {
                                            blockListContent[i, j, k, 3] = 1;
                                            NetBuffer msgBuffer = netServer.CreateBuffer();
                                            msgBuffer = netServer.CreateBuffer();
                                            msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);

                                            msgBuffer.Write((byte)ChatMessageType.SayBlueTeam);
                                            msgBuffer.Write(Defines.Sanitize("Research requires more gold!"));
                                            foreach (NetConnection netConn in playerList.Keys)
                                                if (netConn.Status == NetConnectionStatus.Connected)
                                                    if (playerList[netConn].Team == PlayerTeam.Blue)
                                                        netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
                                        }
                                    }
                                }
                                else
                                {
                                    if (blockListContent[i, j, k, 3] == 0)
                                    {
                                        blockListContent[i, j, k, 3] = 1;
                                        NetBuffer msgBuffer = netServer.CreateBuffer();
                                        msgBuffer = netServer.CreateBuffer();
                                        msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);

                                        msgBuffer.Write((byte)ChatMessageType.SayBlueTeam);
                                        msgBuffer.Write(Defines.Sanitize("Research has halted!"));
                                        foreach (NetConnection netConn in playerList.Keys)
                                            if (netConn.Status == NetConnectionStatus.Connected)
                                                if (playerList[netConn].Team == PlayerTeam.Blue)
                                                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered); 
                                           
                                    }
                                   
                                  //no longer has research topic
                                }
                            }
                            else if (blockList[i, j, k] == BlockType.ResearchR && blockListContent[i, j, k, 0] > 0)
                            {
                                if (blockListContent[i, j, k, 1] > 0)
                                {
                                    if (teamCashRed > 0 && ResearchProgress[(byte)PlayerTeam.Red, blockListContent[i, j, k, 1]] > 0 && blockListContent[i, j, k, 4] == 0)
                                    {
                                        ResearchProgress[(byte)PlayerTeam.Red, blockListContent[i, j, k, 1]]--;

                                        blockListContent[i, j, k, 4] = 3;//timer
                                        blockListContent[i, j, k, 3] = 0;//message warnings
                                        teamCashRed -= 1;

                                        NetBuffer msgBuffer = netServer.CreateBuffer();
                                        foreach (NetConnection netConn in playerList.Keys)
                                            if (netConn.Status == NetConnectionStatus.Connected)
                                                SendTeamCashUpdate(playerList[netConn]);

                                        if (ResearchProgress[(byte)PlayerTeam.Red, blockListContent[i, j, k, 1]] == 0)//research complete
                                        {
                                            ResearchComplete[(byte)PlayerTeam.Red, blockListContent[i, j, k, 1]]++;//increase rank
                                            ResearchProgress[(byte)PlayerTeam.Red, blockListContent[i, j, k, 1]] = ResearchInformation.GetCost((Research)blockListContent[i, j, k, 1]) * (ResearchComplete[(byte)PlayerTeam.Red, blockListContent[i, j, k, 1]] + 1);
                                            NetBuffer msgBufferr = netServer.CreateBuffer();
                                            msgBufferr = netServer.CreateBuffer();
                                            msgBufferr.Write((byte)InfiniminerMessage.ChatMessage);

                                            msgBufferr.Write((byte)ChatMessageType.SayRedTeam);
                                            if (ResearchComplete[(byte)PlayerTeam.Red, blockListContent[i, j, k, 1]] == 1)//first rank
                                                msgBufferr.Write(Defines.Sanitize(ResearchInformation.GetName((Research)blockListContent[i, j, k, 1]) + " research was completed!"));
                                            else
                                                msgBufferr.Write(Defines.Sanitize(ResearchInformation.GetName((Research)blockListContent[i, j, k, 1]) + " rank " + ResearchComplete[(byte)PlayerTeam.Red, blockListContent[i, j, k, 1]] + " research was completed!"));
                                            foreach (NetConnection netConn in playerList.Keys)
                                                if (netConn.Status == NetConnectionStatus.Connected)
                                                    if (playerList[netConn].Team == PlayerTeam.Red)
                                                        netServer.SendMessage(msgBufferr, netConn, NetChannel.ReliableUnordered);

                                            //recalculate player statistics/buffs
                                            ResearchRecalculate(PlayerTeam.Red, blockListContent[i, j, k, 1]);
                                            //
                                            blockListContent[i, j, k, 1] = 0;
                                            blockListContent[i, j, k, 0] = 0;
                                        }
                                    }
                                    else if (blockListContent[i, j, k, 4] > 0)
                                    {
                                        blockListContent[i, j, k, 4]--;
                                    }
                                    else
                                    {
                                        if (blockListContent[i, j, k, 3] == 0)
                                        {
                                            blockListContent[i, j, k, 3] = 1;
                                            NetBuffer msgBuffer = netServer.CreateBuffer();
                                            msgBuffer = netServer.CreateBuffer();
                                            msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);

                                            msgBuffer.Write((byte)ChatMessageType.SayRedTeam);
                                            msgBuffer.Write(Defines.Sanitize("Research requires more gold!"));
                                            foreach (NetConnection netConn in playerList.Keys)
                                                if (netConn.Status == NetConnectionStatus.Connected)
                                                    if (playerList[netConn].Team == PlayerTeam.Red)
                                                        netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
                                        }
                                    }
                                }
                                else
                                {
                                    if (blockListContent[i, j, k, 3] == 0)
                                    {
                                        blockListContent[i, j, k, 3] = 1;
                                        NetBuffer msgBuffer = netServer.CreateBuffer();
                                        msgBuffer = netServer.CreateBuffer();
                                        msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);

                                        msgBuffer.Write((byte)ChatMessageType.SayRedTeam);
                                        msgBuffer.Write(Defines.Sanitize("Research has halted!"));
                                        foreach (NetConnection netConn in playerList.Keys)
                                            if (netConn.Status == NetConnectionStatus.Connected)
                                                if (playerList[netConn].Team == PlayerTeam.Red)
                                                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);

                                    }

                                    //no longer has research topic
                                }
                            }
                            else if (blockList[i, j, k] == BlockType.ResearchR && blockListContent[i, j, k, 0] > 0)
                            {

                            }
                            else if (blockList[i, j, k] == BlockType.BaseBlue || blockList[i, j, k] == BlockType.BaseRed)
                            {
                                foreach (Player p in playerList.Values)
                                {
                                    if (p.Team == PlayerTeam.Blue && blockList[i, j, k] == BlockType.BaseBlue)
                                    {
                                        float distfromBase = (p.Position - new Vector3(i, j + 2, k - 1)).Length();
                                        if (distfromBase < 3)
                                        {
                                            DepositCash(p);
                                            if (p.Health < p.HealthMax && p.Alive)
                                            {
                                                p.Health = p.HealthMax;
                                                SendHealthUpdate(p);
                                            }
                                            //apply block damage buff to prevent walling players in
                                        }
                                        else if (distfromBase < 10)
                                        {
                                            if (p.Health < p.HealthMax && p.Alive)
                                            {
                                                p.Health++;
                                                SendHealthUpdate(p);
                                            }
                                            //apply block damage buff to prevent walling players in
                                        }
                                    }
                                    else if (p.Team == PlayerTeam.Red && blockList[i, j, k] == BlockType.BaseRed)
                                    {
                                        float distfromBase = (p.Position - new Vector3(i, j + 2, k + 1)).Length();
                                        if (distfromBase < 3)
                                        {
                                            DepositCash(p);
                                            //apply block damage buff to prevent walling players in
                                            if (p.Health < p.HealthMax && p.Alive)
                                            {
                                                p.Health = p.HealthMax;
                                                SendHealthUpdate(p);
                                            }
                                        }
                                        else if (distfromBase < 10)
                                        {
                                            //apply block damage buff to prevent walling players in
                                            if (p.Health < p.HealthMax && p.Alive)
                                            {
                                                p.Health++;
                                                SendHealthUpdate(p);
                                            }
                                        }
                                    }
                                }
                                
                                if (blockListContent[i, j, k, 1] > 0)
                                {
                                    if (blockList[i, j, k] == BlockType.BaseBlue)
                                    {
                                        if (teamCashBlue > 4 && blockListContent[i, j, k, 4] == 0)
                                        {
                                            blockListContent[i, j, k, 4] = 3;
                                            blockListContent[i, j, k, 1]--;
                                            blockListContent[i, j, k, 2] = 0;
                                            teamCashBlue -= 5;

                                            NetBuffer msgBuffer = netServer.CreateBuffer();
                                            foreach (NetConnection netConn in playerList.Keys)
                                                if (netConn.Status == NetConnectionStatus.Connected)
                                                    SendTeamCashUpdate(playerList[netConn]);
                                        }
                                        else if (blockListContent[i, j, k, 4] > 0)
                                        {
                                            blockListContent[i, j, k, 4]--;
                                        }
                                        else
                                        {
                                            if (blockListContent[i, j, k, 2] == 0)//warning message
                                            {
                                                blockListContent[i, j, k, 2] = 1;

                                                NetBuffer msgBuffer = netServer.CreateBuffer();
                                                msgBuffer = netServer.CreateBuffer();
                                                msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);

                                                msgBuffer.Write((byte)ChatMessageType.SayBlueTeam);
                                                msgBuffer.Write(Defines.Sanitize("The forge requires " + blockListContent[i, j, k, 1]*5 + " more gold!"));
                                                foreach (NetConnection netConn in playerList.Keys)
                                                    if (netConn.Status == NetConnectionStatus.Connected)
                                                        if (playerList[netConn].Team == PlayerTeam.Blue)
                                                        netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered); 
                                                
                                            }
                                        }
                                    }
                                    else if (blockList[i, j, k] == BlockType.BaseRed)
                                    {
                                        if (teamCashRed > 4 && blockListContent[i, j, k, 4] == 0)
                                        {
                                            blockListContent[i, j, k, 4] = 3;
                                            blockListContent[i, j, k, 1]--;
                                            blockListContent[i, j, k, 2] = 0;
                                            teamCashRed -= 5;

                                            NetBuffer msgBuffer = netServer.CreateBuffer();
                                            foreach (NetConnection netConn in playerList.Keys)
                                                if (netConn.Status == NetConnectionStatus.Connected)
                                                    SendTeamCashUpdate(playerList[netConn]);
                                        }
                                        else if (blockListContent[i, j, k, 4] > 0)
                                        {
                                            blockListContent[i, j, k, 4]--;
                                        }
                                        else
                                        {
                                            if (blockListContent[i, j, k, 2] == 0)//warning message
                                            {
                                                blockListContent[i, j, k, 2] = 1;

                                                NetBuffer msgBuffer = netServer.CreateBuffer();
                                                msgBuffer = netServer.CreateBuffer();
                                                msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);


                                                msgBuffer.Write((byte)ChatMessageType.SayRedTeam);
                                                msgBuffer.Write(Defines.Sanitize("The forge requires " + blockListContent[i, j, k, 1] * 5 + " more gold!"));
                                                foreach (NetConnection netConn in playerList.Keys)
                                                    if (netConn.Status == NetConnectionStatus.Connected)
                                                        if (playerList[netConn].Team == PlayerTeam.Red)
                                                            netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
                                            }
                                        }
                                    }

                                    if (blockListContent[i, j, k, 1] == 0)
                                    {
                                        int arttype = randGen.Next(1, 10);
                                        uint arty = SetItem(ItemType.Artifact, new Vector3(i + 0.5f, j + 1.5f, k + 0.5f), Vector3.Zero, Vector3.Zero, PlayerTeam.None, arttype);

                                        NetBuffer msgBuffer = netServer.CreateBuffer();
                                        msgBuffer = netServer.CreateBuffer();
                                        msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);

                                        if (blockList[i, j, k] == BlockType.BaseRed)
                                            msgBuffer.Write((byte)ChatMessageType.SayRedTeam);
                                        else if (blockList[i, j, k] == BlockType.BaseBlue)
                                            msgBuffer.Write((byte)ChatMessageType.SayBlueTeam);

                                        if (blockList[i, j, k] == BlockType.BaseRed)
                                            msgBuffer.Write(Defines.Sanitize("The " + PlayerTeam.Red + " have formed the " + ArtifactInformation.GetName(arttype) + "!"));
                                        else if (blockList[i, j, k] == BlockType.BaseBlue)
                                            msgBuffer.Write(Defines.Sanitize("The " + PlayerTeam.Blue + " have formed the " + ArtifactInformation.GetName(arttype) + "!"));
                                        foreach (NetConnection netConn in playerList.Keys)
                                            if (netConn.Status == NetConnectionStatus.Connected)
                                                netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered); 
                                    }
                                }
                            }
                    }
        }
        public void Disturb(ushort i, ushort j, ushort k)
        {
            for (ushort a = (ushort)(i-1); a <= 1 + i; a++)
                for (ushort b = (ushort)(j-1); b <= 1 + j; b++)
                    for (ushort c = (ushort)(k-1); c <= 1 + k; c++)
                        if (a > 0 && b > 0 && c > 0 && a < 64 && b < 64 && c < 64)
                        {
                            flowSleep[a, b, c] = false;
                        }
        }
        public BlockType BlockAtPoint(Vector3 point)
        {
            ushort x = (ushort)point.X;
            ushort y = (ushort)point.Y;
            ushort z = (ushort)point.Z;
            if (x <= 0 || y <= 0 || z <= 0 || (int)x > MAPSIZE - 1 || (int)y > MAPSIZE - 1 || (int)z > MAPSIZE - 1)
                return BlockType.None;
            return blockList[x, y, z];
        }

        public bool blockTrace(ushort oX,ushort oY,ushort oZ,ushort dX,ushort dY,ushort dZ,BlockType allow)//only traces x/y not depth
        {
            while (oX != dX || oY != dY || oZ != dZ)
            {
                if (oX - dX > 0)
                {
                    oX = (ushort)(oX - 1);
                    if (blockList[oX, oY, oZ] != allow)
                    {
                        return false;
                    }
                }
                else if (oX - dX < 0)
                {
                    oX = (ushort)(oX + 1);
                    if (blockList[oX, oY, oZ] != allow)
                    {
                        return false;
                    }
                }

                if (oZ - dZ > 0)
                {
                    oZ = (ushort)(oZ - 1);
                    if (blockList[oX, oY, oZ] != allow)
                    {
                        return false;
                    }
                }
                else if (oZ - dZ < 0)
                {
                    oZ = (ushort)(oZ + 1);
                    if (blockList[oX, oY, oZ] != allow)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
        public bool RayCollision(Vector3 startPosition, Vector3 rayDirection, float distance, int searchGranularity, ref Vector3 hitPoint, ref Vector3 buildPoint)
        {
            Vector3 testPos = startPosition;
            Vector3 buildPos = startPosition;
            for (int i = 0; i < searchGranularity; i++)
            {
                testPos += rayDirection * distance / searchGranularity;
                BlockType testBlock = BlockAtPoint(testPos);
                if (testBlock != BlockType.None)
                {
                    hitPoint = testPos;
                    buildPoint = buildPos;
                    return true;
                }
                buildPos = testPos;
            }
            return false;
        }
        public bool RayCollision(Vector3 startPosition, Vector3 rayDirection, float distance, int searchGranularity, BlockType allow)
        {
            Vector3 testPos = startPosition;
            for (int i = 0; i < searchGranularity; i++)
            {
                testPos += rayDirection * distance / searchGranularity;
                BlockType testBlock = BlockAtPoint(testPos);
                if (testBlock != BlockType.None || testBlock != allow)
                {
                    return false;
                }
            }
            return true;
        }

        public bool RayCollision(Vector3 startPosition, Vector3 rayDirection, float distance, int searchGranularity, ref Vector3 hitPoint, ref Vector3 buildPoint, BlockType ignore)
        {
            Vector3 testPos = startPosition;
            Vector3 buildPos = startPosition;
            for (int i = 0; i < searchGranularity; i++)
            {
                testPos += rayDirection * distance / searchGranularity;
                BlockType testBlock = BlockAtPoint(testPos);
                if (testBlock != BlockType.None && testBlock != ignore)
                {
                    hitPoint = testPos;
                    buildPoint = buildPos;
                    return true;
                }
                buildPos = testPos;
            }
            return false;
        }

        public Vector3 RayCollisionExact(Vector3 startPosition, Vector3 rayDirection, float distance, int searchGranularity, ref Vector3 hitPoint, ref Vector3 buildPoint)
        {
            Vector3 testPos = startPosition;
            Vector3 buildPos = startPosition;
           
            for (int i = 0; i < searchGranularity; i++)
            {
                testPos += rayDirection * distance / searchGranularity;
                BlockType testBlock = BlockAtPoint(testPos);
                if (testBlock != BlockType.None)
                {
                    hitPoint = testPos;
                    buildPoint = buildPos;
                    return hitPoint;
                }
                buildPos = testPos;
            }

            return startPosition;
        }

        public Vector3 RayCollisionExactNone(Vector3 startPosition, Vector3 rayDirection, float distance, int searchGranularity, ref Vector3 hitPoint, ref Vector3 buildPoint)
        {//returns a point in space when it reaches distance
            Vector3 testPos = startPosition;
            Vector3 buildPos = startPosition;

            for (int i = 0; i < searchGranularity; i++)
            {
                testPos += rayDirection * distance / searchGranularity;
                BlockType testBlock = BlockAtPoint(testPos);

                if (testBlock != BlockType.None)
                {
                    return startPosition;
                }
            }
            return testPos;
        }
        public void UsePickaxe(Player player, Vector3 playerPosition, Vector3 playerHeading)
        {
            player.QueueAnimationBreak = true;
            
            // Figure out what we're hitting.
            Vector3 hitPoint = Vector3.Zero;
            Vector3 buildPoint = Vector3.Zero;

            if (artifactActive[(byte)player.Team, 4] > 0 || player.Content[10] == 4)
            {
                if (!RayCollision(playerPosition, playerHeading, 2, 10, ref hitPoint, ref buildPoint, BlockType.Water))
                {
                    //ConsoleWrite(player.Handle + " lost a block sync.");
                    return;
                }
            }
            else
            {
                if (!RayCollision(playerPosition, playerHeading, 2, 10, ref hitPoint, ref buildPoint, BlockType.None))
                {
                    //ConsoleWrite(player.Handle + " lost a block sync.");
                    return;
                }
            }
            ushort x = (ushort)hitPoint.X;
            ushort y = (ushort)hitPoint.Y;
            ushort z = (ushort)hitPoint.Z;

            if (player.Alive == false || player.playerToolCooldown > DateTime.Now)
            {
                //ConsoleWrite("fixed " + player.Handle + " synchronization");
                SetBlockForPlayer(x, y, z, blockList[x, y, z], blockCreatorTeam[x, y, z], player);
                return;
            }
            else
            {
                player.playerToolCooldown = DateTime.Now + TimeSpan.FromSeconds((float)(player.GetToolCooldown(PlayerTools.Pickaxe)));
            }
            // Figure out what the result is.
            bool removeBlock = false;
            uint giveOre = 0;
            uint giveCash = 0;
            uint giveWeight = 0;
            int Damage = 2 + ResearchComplete[(byte)player.Team, 5];
            InfiniminerSound sound = InfiniminerSound.DigDirt;
            BlockType block = BlockAtPoint(hitPoint);
            switch (block)
            {
                case BlockType.Lava:
                    if (varGetB("minelava"))
                    {
                        removeBlock = true;
                        sound = InfiniminerSound.DigDirt;
                    }
                    break;
                case BlockType.Water:
                    if (varGetB("minelava"))
                    {
                        removeBlock = true;
                        sound = InfiniminerSound.DigDirt;
                    }
                    break;
                case BlockType.Dirt:
                case BlockType.Mud:
                case BlockType.Grass:
                case BlockType.Sand:
                case BlockType.DirtSign:
                    removeBlock = true;
                    sound = InfiniminerSound.DigDirt;
                    break;
                case BlockType.StealthBlockB:
                    removeBlock = true;
                    sound = InfiniminerSound.DigDirt;
                    break;
                case BlockType.StealthBlockR:
                    removeBlock = true;
                    sound = InfiniminerSound.DigDirt;
                    break;
                case BlockType.TrapB:
                    removeBlock = true;
                    sound = InfiniminerSound.DigDirt;
                    break;
                case BlockType.TrapR:
                    removeBlock = true;
                    sound = InfiniminerSound.DigDirt;
                    break;
                case BlockType.Ore:
                    removeBlock = true;
                    giveOre = 20;
                    sound = InfiniminerSound.DigMetal;
                    break;

                case BlockType.Gold:
                    Damage = 2;
                    giveWeight = 1;
                    giveCash = 10;
                    sound = InfiniminerSound.DigMetal;
                    break;

                case BlockType.Diamond:
                    //removeBlock = true;
                    //giveWeight = 1;
                    //giveCash = 1000;
                    sound = InfiniminerSound.DigMetal;
                    break;

                case BlockType.SolidRed:
                case BlockType.SolidBlue:
                case BlockType.SolidRed2:
                case BlockType.SolidBlue2:
                    sound = InfiniminerSound.DigMetal;
                    break;

                default:
                    break;
            }

            if (giveOre > 0)
            {
                if (player.Ore < player.OreMax - giveOre)
                {
                    player.Ore += giveOre;
                    SendOreUpdate(player);
                }
                else if(player.Ore < player.OreMax)//vaporize some ore to fit into players inventory
                {
                    player.Ore = player.OreMax;
                    SendOreUpdate(player);
                }
                else//ore goes onto ground
                {
                    SetItem(ItemType.Ore, hitPoint - (playerHeading * 0.3f), playerHeading, new Vector3(playerHeading.X * 1.5f, 0.0f, playerHeading.Z * 1.5f), PlayerTeam.None, 0);
                }
            }

            if (giveWeight > 0)
            {
                if (player.Weight < player.WeightMax)
                {
                    player.Weight = Math.Min(player.Weight + giveWeight, player.WeightMax);
                    player.Cash += giveCash;
                    SendWeightUpdate(player);
                    SendCashUpdate(player);
                }
                else
                {
                    removeBlock = false;
                    if (block == BlockType.Gold)
                    {
                        if (player.Weight == player.WeightMax)
                        {
                            //gold goes onto the ground
                            SetItem(ItemType.Gold, hitPoint, playerHeading, new Vector3(playerHeading.X * 1.5f, 0.0f, playerHeading.Z * 1.5f), PlayerTeam.None, 0);
                        }
                    }
                }
            }

            if (removeBlock)//block falls away with any hit
            {
                //SetBlock(x, y, z, BlockType.None, PlayerTeam.None);
                SetBlockDebris(x, y, z, BlockType.None, PlayerTeam.None);//blockset + adds debris for all players
                PlaySoundForEveryoneElse(sound, player.Position, player);
            }
            else if (Damage > 0 && BlockInformation.GetMaxHP(block) > 0)//this block is resistant to pickaxes
            {
                if (blockCreatorTeam[x, y, z] != player.Team)//block does not belong to us: destroy it
                {
                    if (blockListHP[x, y, z] < Damage)
                    {
                        if(block == BlockType.RadarRed)
                        {
                            foreach (Player p in playerList.Values)
                            {
                                if (p.Alive && p.Team == PlayerTeam.Blue)
                                {
                                    if (p.Content[1] == 1)
                                    {
                                        p.Content[1] = 0;//goes off radar again
                                        SendPlayerContentUpdate(p, 1);
                                    }
                                }
                            }
                        }
                        else if(block == BlockType.RadarBlue)
                        { 
                            foreach (Player p in playerList.Values)
                            {
                                if (p.Alive && p.Team == PlayerTeam.Red)
                                {
                                    if (p.Content[1] == 1)
                                    {
                                        p.Content[1] = 0;//goes off radar again
                                        SendPlayerContentUpdate(p, 1);
                                    }
                                }
                            }
                        }
                        else if (block == BlockType.ArtCaseR || block == BlockType.ArtCaseB)
                        {
                            if (y < MAPSIZE - 1)
                                if (blockList[x, y + 1, z] == BlockType.ForceR || blockList[x, y + 1, z] == BlockType.ForceB)
                                {
                                    SetBlock(x, (ushort)(y + 1), z, BlockType.None, PlayerTeam.None);
                                }

                            if (blockListContent[x, y, z, 6] > 0)
                            {
                                uint arty = (uint)(blockListContent[x, y, z, 6]);
                                itemList[arty].Content[6] = 0;//unlock arty
                                SendItemContentSpecificUpdate(itemList[(uint)(blockListContent[x, y, z, 6])], 6);

                                if (blockList[x, y, z] == BlockType.ArtCaseR)
                                    ArtifactTeamBonus(PlayerTeam.Red, itemList[arty].Content[10], false);
                                else if (blockList[x, y, z] == BlockType.ArtCaseB)
                                    ArtifactTeamBonus(PlayerTeam.Blue, itemList[arty].Content[10], false);

                                if (blockList[x, y, z] == BlockType.ArtCaseB)
                                {
                                    teamArtifactsBlue--;
                                    SendScoreUpdate();
                                }
                                else if (blockList[x, y, z] == BlockType.ArtCaseR)
                                {
                                    teamArtifactsRed--;
                                    SendScoreUpdate();
                                }
                            }

                            NetBuffer msgBuffer = netServer.CreateBuffer();
                            msgBuffer = netServer.CreateBuffer();
                            msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);

                            if(block == BlockType.ArtCaseB)
                                msgBuffer.Write((byte)ChatMessageType.SayBlueTeam);
                            else if(block == BlockType.ArtCaseR)
                                msgBuffer.Write((byte)ChatMessageType.SayRedTeam);
                       
                            msgBuffer.Write(Defines.Sanitize("The enemy team has destroyed one of our artifact safehouses!"));

                            foreach (NetConnection netConn in playerList.Keys)
                                if (netConn.Status == NetConnectionStatus.Connected)
                                    if (playerList[netConn].Team == PlayerTeam.Red && block == BlockType.ArtCaseR)
                                    {
                                        netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
                                    }
                                    else if (playerList[netConn].Team == PlayerTeam.Blue && block == BlockType.ArtCaseB)
                                    {
                                        netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
                                    }

                        }
                        else if (block == BlockType.Diamond)
                        {
                            uint piece = SetItem(ItemType.Diamond, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), Vector3.Zero, Vector3.Zero, PlayerTeam.None, 0);
                        }

                        SetBlockDebris(x, y, z, BlockType.None, PlayerTeam.None);//blockset + adds debris for all players
                        
                        blockListHP[x, y, z] = 0;
                        sound = InfiniminerSound.Explosion;
                    }
                    else
                    {
                        hitPoint -= playerHeading * 0.3f;

                        DebrisEffectAtPoint(hitPoint.X, hitPoint.Y, hitPoint.Z, block, 0);

                        blockListHP[x, y, z] -= Damage;
                        hitPoint -= (playerHeading*0.4f);

                        if (block == BlockType.SolidRed2 || block == BlockType.SolidBlue2)
                        {
                            if (blockListHP[x, y, z] < 21)
                            {
                                SetBlock(x, y, z, blockCreatorTeam[x, y, z] == PlayerTeam.Red ? BlockType.SolidRed : BlockType.SolidBlue, blockCreatorTeam[x, y, z]);
                            }
                        }
                        else if (block == BlockType.Gold)
                        {
                            PlaySoundForEveryoneElse(InfiniminerSound.RadarLow, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), player);
                            //InfiniminerSound.RadarHigh
                        }
                        else if (block == BlockType.Diamond)
                        {
                            PlaySoundForEveryoneElse(InfiniminerSound.RadarHigh, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), player);
                        }

                        if (artifactActive[(byte)blockCreatorTeam[x, y, z], 7] > 0)//reflection artifact
                        {
                            if (player.Health > 2 * artifactActive[(byte)blockCreatorTeam[x, y, z], 7])
                            {
                                player.Health -= (uint)(2 * artifactActive[(byte)blockCreatorTeam[x, y, z], 7]);
                                SendHealthUpdate(player);
                            }
                            else
                            {
                                Player_Dead(player, "BEAT THEMSELVES AGAINST A WALL!");
                            }
                        }
                    }

                    PlaySoundForEveryoneElse(sound, player.Position, player);
                }
                else
                {
                    if (player.Ore > ResearchComplete[(byte)player.Team, 4])//make repairs
                    {
                        Damage = -(2 * ResearchComplete[(byte)player.Team, 4] + 2);
                        //sound = repair?

                        if (blockListHP[x, y, z] >= BlockInformation.GetMaxHP(blockList[x, y, z]))
                        {
                            if (block == BlockType.SolidRed || block == BlockType.SolidBlue)
                            {
                                hitPoint -= playerHeading * 0.3f;
                                player.Ore -= (uint)ResearchComplete[(byte)player.Team, 4] + 1;
                                blockListHP[x, y, z] -= Damage;
                                DebrisEffectAtPoint(hitPoint.X, hitPoint.Y, hitPoint.Z, block, 0);
                                SetBlock(x, y, z, player.Team == PlayerTeam.Red ? BlockType.SolidRed2 : BlockType.SolidBlue2, player.Team);
                                SendOreUpdate(player);
                                PlaySoundForEveryoneElse(sound, player.Position, player);
                            }
                            else if (block == BlockType.ConstructionR && player.Team == PlayerTeam.Red || block == BlockType.ConstructionB && player.Team == PlayerTeam.Blue)//construction complete
                            {
                                SetBlock(x, (ushort)(y + 1), z, player.Team == PlayerTeam.Red ? BlockType.ForceR : BlockType.ForceB, player.Team);
                                SetBlock(x, y, z, (BlockType)blockListContent[x,y,z,0], player.Team);
                                blockListHP[x, y, z] = BlockInformation.GetMaxHP(blockList[x, y, z]);
                            }
                        }
                        else
                        {
                            hitPoint -= playerHeading * 0.3f;
                            player.Ore -= (uint)ResearchComplete[(byte)player.Team, 4] + 1;
                            DebrisEffectAtPoint(hitPoint.X, hitPoint.Y, hitPoint.Z, block, 0);
                            blockListHP[x, y, z] -= Damage;
                            SendOreUpdate(player);
                            PlaySoundForEveryoneElse(sound, player.Position, player);
                        }
                    }
                }
            }
            else
            {//player was out of sync, replace his empty block
                //ConsoleWrite("fixed " + player.Handle + " synchronization");
                SetBlockForPlayer(x, y, z, block, blockCreatorTeam[x, y, z], player);
            }
        }

        public void UseSmash(Player player, Vector3 playerPosition, Vector3 playerHeading)
        {

        }

        public void UseStrongArm(Player player, Vector3 playerPosition, Vector3 playerHeading)
        {
            player.QueueAnimationBreak = true;
            Vector3 headPosition = playerPosition + new Vector3(0f, 0.1f, 0f);
            // Figure out what we're hitting.
            Vector3 hitPoint = Vector3.Zero;
            Vector3 buildPoint = Vector3.Zero;

            if (player.Content[5] == 0)
                if (!RayCollision(playerPosition, playerHeading, 2, 10, ref hitPoint, ref buildPoint, BlockType.Water))
                    return;

            if (player.Content[5] > 0)
            {
                //Vector3 throwPoint = RayCollisionExact(playerPosition, playerHeading, 10, 100, ref hitPoint, ref buildPoint);
                //if (throwPoint != playerPosition)
                //{
                    //double dist = Distf(playerPosition, throwPoint);
                    //if (dist < 2)
                     //   return;//distance of ray should be strength
                    //else
                    {
                        //begin throw
                        buildPoint = headPosition + (playerHeading*2);
                            //RayCollisionExactNone(playerPosition, playerHeading, 2, 10, ref hitPoint, ref buildPoint);
                        //
                    }
              //  }
            }
            ushort x = (ushort)hitPoint.X;
            ushort y = (ushort)hitPoint.Y;
            ushort z = (ushort)hitPoint.Z;
            // Figure out what the result is.
            bool grabBlock = false;

            if (player.Content[5] == 0)
            {
                uint giveWeight = 0;
                InfiniminerSound sound = InfiniminerSound.DigDirt;

                BlockType block = BlockAtPoint(hitPoint);
                switch (block)
                {

                    case BlockType.Dirt:
                    case BlockType.Grass:
                    case BlockType.Pump:
                    case BlockType.Barrel:
                    case BlockType.Pipe:
                    case BlockType.Rock:
                    case BlockType.Mud:
                    case BlockType.Sand:
                    case BlockType.DirtSign:
                    case BlockType.StealthBlockB:
                    case BlockType.StealthBlockR:
                    case BlockType.TrapB:
                    case BlockType.TrapR:
                    case BlockType.Ore:
                    case BlockType.Explosive:
                        grabBlock = true;
                        giveWeight = 10;
                        sound = InfiniminerSound.DigMetal;
                        break;
                    case BlockType.SolidBlue:
                        if (player.Team == PlayerTeam.Blue)
                        {
                            grabBlock = true;
                            giveWeight = 10;
                            sound = InfiniminerSound.DigMetal;
                        }
                        break;
                    case BlockType.SolidRed:
                        if (player.Team == PlayerTeam.Red)
                        {
                            grabBlock = true;
                            giveWeight = 10;
                            sound = InfiniminerSound.DigMetal;
                        }
                        break;
                }

                if (blockCreatorTeam[x, y, z] == PlayerTeam.Blue && player.Team == PlayerTeam.Red)
                {
                    return;//dont allow enemy team to manipulate other teams team-blocks
                }
                else if (blockCreatorTeam[x, y, z] == PlayerTeam.Red && player.Team == PlayerTeam.Blue)
                {
                    return;
                }

                if (giveWeight > 0)
                {
                    if (player.Weight + giveWeight <= player.WeightMax)
                    {
                        player.Weight += giveWeight;
                        SendWeightUpdate(player);
                    }
                    else
                    {
                        grabBlock = false;
                    }
                }

                if (grabBlock)
                {
                    player.Content[5] = (byte)block;
                    for (uint cc = 0; cc < 20; cc++)//copy the content values
                    {
                        player.Content[50 + cc] = blockListContent[x, y, z, cc];//50 is past players accessible content, it is for server only
                    }

                    if (block == BlockType.Explosive)//must update player explosive keys
                    {                        
                        foreach (Player p in playerList.Values)
                        {
                            int cc = p.ExplosiveList.Count;

                            int ca = 0;
                            while(ca < cc)
                            {
                                if (p.ExplosiveList[ca].X == x && p.ExplosiveList[ca].Y == y && p.ExplosiveList[ca].Z == z)
                                {
                                    player.Content[50 + 17] = (int)p.ID;
                                    p.ExplosiveList.RemoveAt(ca);//experimental
                                    break;
                                }
                                ca += 1;
                            }
                        }

                    }

                    SendContentSpecificUpdate(player,5);
                    SetBlock(x, y, z, BlockType.None, PlayerTeam.None);
                    PlaySound(sound, player.Position);
                }
            }
            else
            {//throw the block
                BlockType block = (BlockType)(player.Content[5]);
                if (block != BlockType.None)
                {
                    ushort bx = (ushort)buildPoint.X;
                    ushort by = (ushort)buildPoint.Y;
                    ushort bz = (ushort)buildPoint.Z;
                    if (blockList[bx, by, bz] == BlockType.None)
                    {
                        SetBlock(bx, by, bz, block, PlayerTeam.None);
                        player.Weight -= 10;
                        player.Content[5] = 0;
                        SendWeightUpdate(player);
                        SendContentSpecificUpdate(player, 5);
                        for (uint cc = 0; cc < 20; cc++)//copy the content values
                        {
                            blockListContent[bx, by, bz, cc] = player.Content[50 + cc];
                            if (cc == 17 && block == BlockType.Explosive)//explosive list for tnt update
                            {
                                foreach (Player p in playerList.Values)
                                {
                                    if (p.ID == (uint)(blockListContent[bx, by, bz, cc]))
                                    {
                                        //found explosive this belongs to
                                        p.ExplosiveList.Add(new Vector3(bx,by,bz));
                                    }
                                }
                            }
                            player.Content[50 + cc] = 0;
                        }

                        blockListContent[bx, by, bz, 10] = 1;//undergoing gravity changes 
                        blockListContent[bx, by, bz, 11] = (int)((playerHeading.X*1.2)*100);//1.2 = throw strength
                        blockListContent[bx, by, bz, 12] = (int)((playerHeading.Y*1.2)*100);
                        blockListContent[bx, by, bz, 13] = (int)((playerHeading.Z*1.2)*100);
                        blockListContent[bx, by, bz, 14] = (int)((buildPoint.X) * 100);
                        blockListContent[bx, by, bz, 15] = (int)((buildPoint.Y) * 100);
                        blockListContent[bx, by, bz, 16] = (int)((buildPoint.Z) * 100);

                        blockCreatorTeam[bx, by, bz] = player.Team;
                        PlaySound(InfiniminerSound.GroundHit, player.Position);
                    }
                }
            }
        }
        //private bool LocationNearBase(ushort x, ushort y, ushort z)
        //{
        //    for (int i=0; i<MAPSIZE; i++)
        //        for (int j=0; j<MAPSIZE; j++)
        //            for (int k = 0; k < MAPSIZE; k++)
        //                if (blockList[i, j, k] == BlockType.HomeBlue || blockList[i, j, k] == BlockType.HomeRed)
        //                {
        //                    double dist = Math.Sqrt(Math.Pow(x - i, 2) + Math.Pow(y - j, 2) + Math.Pow(z - k, 2));
        //                    if (dist < 3)
        //                        return true;
        //                }
        //    return false;
        //}
        public void ThrowRope(Player player, Vector3 playerPosition, Vector3 playerHeading)
        {
            bool actionFailed = false;

            if (player.Alive == false || player.playerToolCooldown > DateTime.Now)
            {
                actionFailed = true;
            }
            else if (player.Ore > 49)
            {
                player.Ore -= 50;
                SendOreUpdate(player);
            }
            else
            {
                actionFailed = true;
            }
            // If there's no surface within range, bail.
            Vector3 hitPoint = playerPosition;
            Vector3 buildPoint = playerPosition;
            Vector3 exactPoint = playerPosition;
          
            ushort x = (ushort)buildPoint.X;
            ushort y = (ushort)buildPoint.Y;
            ushort z = (ushort)buildPoint.Z;

            if (x <= 0 || y <= 0 || z <= 0 || (int)x > MAPSIZE - 1 || (int)y > MAPSIZE - 1 || (int)z > MAPSIZE - 1)
                actionFailed = true;

            if (blockList[(ushort)hitPoint.X, (ushort)hitPoint.Y, (ushort)hitPoint.Z] == BlockType.Lava || blockList[(ushort)hitPoint.X, (ushort)hitPoint.Y, (ushort)hitPoint.Z] == BlockType.Water)
                actionFailed = true;

            if (actionFailed)
            {
                // Decharge the player's gun.
                //    TriggerConstructionGunAnimation(player, -0.2f);
            }
            else
            {
                player.playerToolCooldown = DateTime.Now + TimeSpan.FromSeconds((float)(player.GetToolCooldown(PlayerTools.ThrowRope)));
                // Fire the player's gun.
                //    TriggerConstructionGunAnimation(player, 0.5f);

                // Build the block.
                //hitPoint = RayCollision(playerPosition, playerHeading, 6, 25, ref hitPoint, ref buildPoint, 1);

                exactPoint.Y = exactPoint.Y + (float)0.25;//0.25 = items height

                uint ii = SetItem(ItemType.Rope, exactPoint, playerHeading, playerHeading * 5, player.Team, 0);
                itemList[ii].Content[6] = (byte)player.Team;//set teamsafe
                // player.Ore -= blockCost;
                // SendResourceUpdate(player);

                // Play the sound.
                PlaySound(InfiniminerSound.ConstructionGun, player.Position);
            }


        }
        public void ThrowBomb(Player player, Vector3 playerPosition, Vector3 playerHeading)
        {
            bool actionFailed = false;

            if (player.Alive == false || player.playerToolCooldown > DateTime.Now)
            {
                actionFailed = true;
            }
            else if (player.Ore > 49)
            {
                player.Ore -= 50;
                SendOreUpdate(player);
            }
            else
            {
                actionFailed = true;
            }
            // If there's no surface within range, bail.
            Vector3 hitPoint = playerPosition;//Vector3.Zero;
            Vector3 buildPoint = playerPosition;
            Vector3 exactPoint = playerPosition;
            //if (!RayCollision(playerPosition, playerHeading, 6, 25, ref hitPoint, ref buildPoint))
            //{
            //    actionFailed = true;
            //}
            //else
            //{
            //    exactPoint = RayCollisionExact(playerPosition, playerHeading, 6, 25, ref hitPoint, ref buildPoint);
            //}
            ushort x = (ushort)buildPoint.X;
            ushort y = (ushort)buildPoint.Y;
            ushort z = (ushort)buildPoint.Z;

            if (x <= 0 || y <= 0 || z <= 0 || (int)x > MAPSIZE - 1 || (int)y > MAPSIZE - 1 || (int)z > MAPSIZE - 1)
                actionFailed = true;

            if (blockList[(ushort)hitPoint.X, (ushort)hitPoint.Y, (ushort)hitPoint.Z] == BlockType.Lava || blockList[(ushort)hitPoint.X, (ushort)hitPoint.Y, (ushort)hitPoint.Z] == BlockType.Water)
                actionFailed = true;

            if (actionFailed)
            {
                // Decharge the player's gun.
            //    TriggerConstructionGunAnimation(player, -0.2f);
            }
            else
            {
                player.playerToolCooldown = DateTime.Now + TimeSpan.FromSeconds((float)(player.GetToolCooldown(PlayerTools.ThrowBomb)));
                // Fire the player's gun.
            //    TriggerConstructionGunAnimation(player, 0.5f);

                // Build the block.
                //hitPoint = RayCollision(playerPosition, playerHeading, 6, 25, ref hitPoint, ref buildPoint, 1);

                exactPoint.Y = exactPoint.Y + (float)0.25;//0.25 = items height

                uint ii = SetItem(ItemType.Bomb, exactPoint, playerHeading, playerHeading*3, player.Team, 0);
                itemList[ii].Content[6] = (byte)player.Team;//set teamsafe
               // player.Ore -= blockCost;
               // SendResourceUpdate(player);

                // Play the sound.
                PlaySound(InfiniminerSound.ConstructionGun, player.Position);
            }            


        }
        public void UseConstructionGun(Player player, Vector3 playerPosition, Vector3 playerHeading, BlockType blockType)
        {
            bool actionFailed = false;
            bool constructionRequired = false;
            // If there's no surface within range, bail.
            Vector3 hitPoint = Vector3.Zero;
            Vector3 buildPoint = Vector3.Zero;
            if (!RayCollision(playerPosition, playerHeading, 6, 25, ref hitPoint, ref buildPoint,BlockType.Water))
                actionFailed = true;

            // If the block is too expensive, bail.
            uint blockCost = BlockInformation.GetCost(blockType);
            
            if (varGetB("sandbox") && blockCost <= player.OreMax)
                blockCost = 0;
            if (blockCost > player.Ore)
                actionFailed = true;
            if (!allowBlock[(byte)player.Team, (byte)player.Class, (byte)blockType])
                actionFailed = true;
            // If there's someone there currently, bail.
            ushort x = (ushort)buildPoint.X;
            ushort y = (ushort)buildPoint.Y;
            ushort z = (ushort)buildPoint.Z;
            foreach (Player p in playerList.Values)
            {
                if ((int)p.Position.X == x && (int)p.Position.Z == z && ((int)p.Position.Y == y || (int)p.Position.Y - 1 == y))
                    actionFailed = true;
            }

            // If it's out of bounds, bail.
            if (x <= 0 || y <= 0 || z <= 0 || (int)x > MAPSIZE - 1 || (int)y >= MAPSIZE - 1 || (int)z > MAPSIZE - 1)//y >= prevent blocks going too high on server
                actionFailed = true;

            // If it's near a base, bail.
            //if (LocationNearBase(x, y, z))
            //    actionFailed = true;

            // If it's lava, don't let them build off of lava.
            if (blockList[(ushort)hitPoint.X, (ushort)hitPoint.Y, (ushort)hitPoint.Z] == BlockType.Lava || blockList[(ushort)hitPoint.X, (ushort)hitPoint.Y, (ushort)hitPoint.Z] == BlockType.Water)
                actionFailed = true;

            if(!actionFailed)
                if (blockType == BlockType.ArtCaseR || blockType == BlockType.ArtCaseB)
            {//space above must be cleared
                constructionRequired = true;
                //if (blockList[(ushort)hitPoint.X, (ushort)hitPoint.Y + 1, (ushort)hitPoint.Z] != BlockType.None)
                //{
                //    actionFailed = true;
                //}
                //else
                //{
                //    SetBlock(x, (ushort)(y+1), z, BlockType.Vacuum, player.Team);//space for artifact
                //}
            }

            if (actionFailed)
            {
                // Decharge the player's gun.
                TriggerConstructionGunAnimation(player, -0.2f);
            }
            else
            {
                // Fire the player's gun.
                TriggerConstructionGunAnimation(player, 0.5f);

                // Build the block.
               // if (blockType == BlockType.Lava)
                    //blockType = BlockType.Fire;

                if (constructionRequired == true)//block changes into construction block with blocktype on content[0]
                {
                    if (blockType == BlockType.ArtCaseR || blockType == BlockType.ArtCaseB)
                    {
                        //check above for space
                        if (blockList[x, y+1, z] == BlockType.None)
                        {
                            blockList[x, y+1, z] = BlockType.Vacuum;//player cant see, dont bother updating for client
                        }
                        else
                        {
                            return;//wasnt space for the glass
                        }
                    }

                    if (player.Team == PlayerTeam.Red)
                    {
                        SetBlock(x, y, z, BlockType.ConstructionR, player.Team);
                    }
                    else if (player.Team == PlayerTeam.Blue)
                    {
                        SetBlock(x, y, z, BlockType.ConstructionB, player.Team);
                    }
                    blockListHP[x, y, z] = BlockInformation.GetHP(blockType);//base block hp
                    blockListContent[x, y, z, 0] = (byte)blockType;

                    if (blockType == BlockType.ArtCaseR || blockType == BlockType.ArtCaseB)
                    {
                        blockListContent[x, y, z, 6] = 0;
                    }
                }
                else
                {
                    if(blockType == BlockType.Metal)
                        SetBlock(x, y, z, blockType, PlayerTeam.None);
                    else
                    SetBlock(x, y, z, blockType, player.Team);

                    if (BlockInformation.GetMaxHP(blockType) > 0)
                    {
                        blockListHP[x, y, z] = BlockInformation.GetHP(blockType);//base block hp
                    }
                }

                player.Ore -= blockCost;
                SendOreUpdate(player);
                //SendResourceUpdate(player);

                // Play the sound.
                PlaySound(InfiniminerSound.ConstructionGun, player.Position);

                // If it's an explosive block, add it to our list.
                if (blockType == BlockType.Explosive)
                    player.ExplosiveList.Add(new Vector3(x,y,z) );
            }            
        }

        public void UseDeconstructionGun(Player player, Vector3 playerPosition, Vector3 playerHeading)
        {
            bool actionFailed = false;

            // If there's no surface within range, bail.
            Vector3 hitPoint = Vector3.Zero;
            Vector3 buildPoint = Vector3.Zero;
            if (!RayCollision(playerPosition, playerHeading, 6, 25, ref hitPoint, ref buildPoint, BlockType.Water))
                actionFailed = true;
            ushort x = (ushort)hitPoint.X;
            ushort y = (ushort)hitPoint.Y;
            ushort z = (ushort)hitPoint.Z;

            // If this is another team's block, bail.
            if (blockCreatorTeam[x, y, z] != player.Team)
                actionFailed = true;

            BlockType blockType = blockList[x, y, z];
            if (!(blockType == BlockType.SolidBlue ||
                blockType == BlockType.SolidRed ||
                blockType == BlockType.SolidBlue2 ||
                blockType == BlockType.SolidRed2 ||
                blockType == BlockType.BankBlue ||
                blockType == BlockType.BankRed ||
                blockType == BlockType.ArtCaseR ||
                blockType == BlockType.ArtCaseB ||
                blockType == BlockType.Jump ||
                blockType == BlockType.Ladder ||
                blockType == BlockType.Road ||
                blockType == BlockType.Shock ||
                blockType == BlockType.ResearchR ||
                blockType == BlockType.ResearchB ||
                blockType == BlockType.BeaconRed ||
                blockType == BlockType.BeaconBlue ||
                blockType == BlockType.Water ||
                blockType == BlockType.TransBlue ||
                blockType == BlockType.TransRed ||
                blockType == BlockType.GlassR ||
                blockType == BlockType.GlassB ||
                blockType == BlockType.Generator ||
                blockType == BlockType.Pipe ||
                blockType == BlockType.Pump ||
                blockType == BlockType.RadarBlue ||
                blockType == BlockType.RadarRed ||
                blockType == BlockType.Barrel ||
                blockType == BlockType.Hinge ||
                blockType == BlockType.Lever ||
                blockType == BlockType.Plate ||
                blockType == BlockType.Controller ||
                blockType == BlockType.Water ||
                blockType == BlockType.StealthBlockB ||
                blockType == BlockType.StealthBlockR ||
                blockType == BlockType.TrapB ||
                blockType == BlockType.TrapR 
                ))
                actionFailed = true;

            if (actionFailed)
            {
                // Decharge the player's gun.
                TriggerConstructionGunAnimation(player, -0.2f);
            }
            else
            {
                // Fire the player's gun.
                TriggerConstructionGunAnimation(player, 0.5f);

                if (blockType == BlockType.RadarRed)//requires special remove
                {
                    foreach (Player p in playerList.Values)
                    {
                        if (p.Alive && p.Team == PlayerTeam.Blue) 
                            {
                                if (p.Content[1] == 1)
                                {
                                    p.Content[1] = 0;//goes off radar again
                                    SendPlayerContentUpdate(p, 1);
                                }
                            }
                    }
                }
                else if (blockType == BlockType.RadarBlue)//requires special remove
                {
                    foreach (Player p in playerList.Values)
                    {
                        if (p.Alive && p.Team == PlayerTeam.Red)
                        {
                            if (p.Content[1] == 1)
                            {
                                p.Content[1] = 0;//goes off radar again
                                SendPlayerContentUpdate(p, 1);
                            }
                        }
                    }
                }
                else if (blockType == BlockType.ConstructionR || blockType == BlockType.ConstructionB)
                {
                    if (blockListContent[x, y, z, 0] == (byte)BlockType.ArtCaseR || blockListContent[x, y, z, 0] == (byte)BlockType.ArtCaseB)
                    {
                        if (y < MAPSIZE - 1)
                            if (blockList[x, y + 1, z] == BlockType.Vacuum)
                                blockList[x, y + 1, z] = BlockType.None;//restore vacuum to normal
                    }

                }
                else if (blockType == BlockType.ArtCaseR || blockType == BlockType.ArtCaseB)
                {
                    if (y < MAPSIZE - 1)
                        if (blockList[x, y + 1, z] == BlockType.ForceR || blockList[x, y + 1, z] == BlockType.ForceB)
                        {
                            SetBlock(x, (ushort)(y + 1), z, BlockType.None, PlayerTeam.None);//remove field
                        }

                    if (blockListContent[x, y, z, 6] > 0)
                    {
                        uint arty = (uint)(blockListContent[x, y, z, 6]);
                        itemList[arty].Content[6] = 0;//unlock arty
                        SendItemContentSpecificUpdate(itemList[(uint)(blockListContent[x, y, z, 6])], 6);

                        if (blockList[x, y, z] == BlockType.ArtCaseB)
                        {
                            teamArtifactsBlue--;
                            SendScoreUpdate();
                        }
                        else if (blockList[x, y, z] == BlockType.ArtCaseR)
                        {
                            teamArtifactsRed--;
                            SendScoreUpdate();
                        }
                    }
                }
                // Remove the block.
                SetBlock(x, y, z, BlockType.None, PlayerTeam.None);
                PlaySound(InfiniminerSound.ConstructionGun, player.Position);
            }
        }

        public void TriggerConstructionGunAnimation(Player player, float animationValue)
        {
            if (player.NetConn.Status != NetConnectionStatus.Connected)
                return;

            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.TriggerConstructionGunAnimation);
            msgBuffer.Write(animationValue);
            netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder1);
        }

        public void UseSignPainter(Player player, Vector3 playerPosition, Vector3 playerHeading)
        {
            // If there's no surface within range, bail.
            Vector3 hitPoint = Vector3.Zero;
            Vector3 buildPoint = Vector3.Zero;
            if (!RayCollision(playerPosition, playerHeading, 4, 25, ref hitPoint, ref buildPoint))
                return;
            ushort x = (ushort)hitPoint.X;
            ushort y = (ushort)hitPoint.Y;
            ushort z = (ushort)hitPoint.Z;

            if (blockList[x, y, z] == BlockType.Dirt)
            {
                SetBlock(x, y, z, BlockType.DirtSign, PlayerTeam.None);
                PlaySound(InfiniminerSound.ConstructionGun, player.Position);
            }
            else if (blockList[x, y, z] == BlockType.DirtSign)
            {
                SetBlock(x, y, z, BlockType.Dirt, PlayerTeam.None);
                PlaySound(InfiniminerSound.ConstructionGun, player.Position);
            }
        }

        public void ExplosionEffectAtPoint(float x, float y, float z, int strength, PlayerTeam team)
        {
            //SetBlock((ushort)x, (ushort)y, (ushort)z, BlockType.Fire, PlayerTeam.None);//might be better at detonate
            //blockListContent[x, y, z, 0] = 6;//fire gets stuck?
            double dist = 0.0f;
            uint damage = 0;

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected && playerList[netConn].Alive)
                {
                    if(playerList[netConn].Alive)//needs teamcheck
                    if (playerList[netConn].Team != team)
                    {
                        dist = Distf(playerList[netConn].Position, new Vector3(x, y, z));
                        if (dist <= strength)//player in range of bomb on server?
                        {
                            damage = (uint)((strength*10) - (dist*10));//10 dmg per dist
                            if (playerList[netConn].Health > damage)
                            {
                                playerList[netConn].Health -= damage;
                                SendHealthUpdate(playerList[netConn]);

                                NetBuffer msgBufferB = netServer.CreateBuffer();
                                msgBufferB.Write((byte)InfiniminerMessage.PlayerSlap);
                                msgBufferB.Write(playerList[netConn].ID);//getting slapped
                                msgBufferB.Write((uint)0);//attacker
                                SendHealthUpdate(playerList[netConn]);

                                foreach (NetConnection netConnB in playerList.Keys)
                                    if (netConnB.Status == NetConnectionStatus.Connected)
                                        netServer.SendMessage(msgBufferB, netConnB, NetChannel.ReliableUnordered);
                            }
                            else
                            {
                                Player_Dead(playerList[netConn],"EXPLODED!");
                            }
                        }
                    }
                }

            // Send off the explosion to clients.
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.TriggerExplosion);
            msgBuffer.Write(new Vector3(x, y, z));
            msgBuffer.Write(strength);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
        }

        public void EffectAtPoint(Vector3 pos, uint efftype)//integer designed to be blocked inside block
        {

            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.Effect);
            msgBuffer.Write(pos);
            msgBuffer.Write(efftype);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
        }

        public void DebrisEffectAtPoint(int x, int y, int z, BlockType block, int efftype)//integer designed to be blocked inside block
        {
            //0 = hit
            //1 = block specific effect
            
            /*
             Vector3 blockPos = msgBuffer.ReadVector3();
             BlockType blockType = (BlockType)msgBuffer.ReadByte();
             uint debrisType = msgBuffer.ReadUInt32();
             */
            // Send off the explosion to clients.

            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.TriggerDebris);
            msgBuffer.Write(new Vector3(x+0.5f, y+0.5f, z+0.5f));
            msgBuffer.Write((byte)(block));
            msgBuffer.Write(efftype);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
            //Or not, there's no dedicated function for this effect >:(
        }
        public void DebrisEffectAtPoint(float x, float y, float z, BlockType block, int efftype)//float is exact
        {
            //0 = hit
            //1 = block specific effect

            /*
             Vector3 blockPos = msgBuffer.ReadVector3();
             BlockType blockType = (BlockType)msgBuffer.ReadByte();
             uint debrisType = msgBuffer.ReadUInt32();
             */
            // Send off the explosion to clients.
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.TriggerDebris);
            msgBuffer.Write(new Vector3(x, y, z));
            msgBuffer.Write((byte)(block));
            msgBuffer.Write(efftype);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
            //Or not, there's no dedicated function for this effect >:(
        }
        public void EarthquakeEffectAtPoint(int x, int y, int z, int strength)
        {
            // Send off the explosion to clients.
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.TriggerEarthquake);
            msgBuffer.Write(new Vector3(x, y, z));
            msgBuffer.Write(strength);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
        }

        public void BombAtPoint(int x, int y, int z, PlayerTeam team)
        {
            ExplosionEffectAtPoint(x, y, z, 3, team);

            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (x + dx <= 0 || y + dy <= 0 || z + dz <= 0 || x + dx > MAPSIZE - 1 || y + dy > MAPSIZE - 1 || z + dz > MAPSIZE - 1)
                            continue;

                        if (blockList[x + dx, y + dy, z + dz] == BlockType.Explosive)
                            if (blockCreatorTeam[x + dx, y + dy, z + dz] != team)//must hit opposing team
                                DetonateAtPoint(x + dx, y + dy, z + dz);

                        if(BlockInformation.GetMaxHP(blockList[x + dx, y + dy, z + dz]) > 0)//not immune block
                            if (blockCreatorTeam[x + dx, y + dy, z + dz] != team)//must hit opposing team
                        if (blockListHP[x + dx, y + dy, z + dz] > 0)
                        {

                            if (blockList[x + dx, y + dy, z + dz] == BlockType.Gold || blockList[x + dx, y + dy, z + dz] == BlockType.Diamond)
                            {//these blocks immune to explosives
                                
                            }
                            else
                            {

                                blockListHP[x + dx, y + dy, z + dz] -= 10;

                                if (blockListHP[x + dx, y + dy, z + dz] <= 0)
                                {
                                    blockListHP[x + dx, y + dy, z + dz] = 0;
                                    if (blockList[x + dx, y + dy, z + dz] == BlockType.RadarRed)//requires special remove
                                    {
                                        foreach (Player p in playerList.Values)
                                        {
                                            if (p.Alive && p.Team == PlayerTeam.Blue)
                                            {
                                                if (p.Content[1] == 1)
                                                {
                                                    p.Content[1] = 0;//goes off radar again
                                                    SendPlayerContentUpdate(p, 1);
                                                }
                                            }
                                        }
                                    }
                                    else if (blockList[x + dx, y + dy, z + dz] == BlockType.RadarBlue)//requires special remove
                                    {
                                        foreach (Player p in playerList.Values)
                                        {
                                            if (p.Alive && p.Team == PlayerTeam.Red)
                                            {
                                                if (p.Content[1] == 1)
                                                {
                                                    p.Content[1] = 0;//goes off radar again
                                                    SendPlayerContentUpdate(p, 1);
                                                }
                                            }
                                        }
                                    }
                                    else if (blockList[x + dx, y + dy, z + dz] == BlockType.Ore)
                                    {//item creation must be outside item loop
                                        SetItem(ItemType.Ore, new Vector3(x, y, z), Vector3.Zero, Vector3.Zero, PlayerTeam.None, 0);
                                    }

                                    SetBlockDebris((ushort)(x + dx), (ushort)(y + dy), (ushort)(z + dz), BlockType.None, PlayerTeam.None);
                                }
                                else
                                {
                                    if (blockList[x + dx, y + dy, z + dz] == BlockType.Rock)//rock is weak to explosives
                                    {//item creation must be outside item loop
                                        SetBlockDebris((ushort)(x + dx), (ushort)(y + dy), (ushort)(z + dz), BlockType.None, PlayerTeam.None);
                                    }
                                }
                            }
                        }
                    }
        }

        public void DetonateAtPoint(int x, int y, int z)
        {
            // Remove the block that is detonating.
            PlayerTeam team = blockCreatorTeam[(ushort)(x), (ushort)(y), (ushort)(z)];
            SetBlock((ushort)(x), (ushort)(y), (ushort)(z), BlockType.None, PlayerTeam.None);

            // Remove this from any explosive lists it may be in.
            foreach (Player p in playerList.Values)
                p.ExplosiveList.Remove(new Vector3(x, y, z));

            // Detonate the block.
            if (!varGetB("stnt"))
            {
                for (int dx = -2; dx <= 2; dx++)
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dz = -2; dz <= 2; dz++)
                        {
                            // Check that this is a sane block position.
                            if (x + dx <= 0 || y + dy <= 0 || z + dz <= 0 || x + dx > MAPSIZE - 1 || y + dy > MAPSIZE - 1 || z + dz > MAPSIZE - 1)
                                continue;

                            // Chain reactions!
                            if (blockList[x + dx, y + dy, z + dz] == BlockType.Explosive)
                                DetonateAtPoint(x + dx, y + dy, z + dz);

                            // Detonation of normal blocks.
                            bool destroyBlock = false;
                            switch (blockList[x + dx, y + dy, z + dz])
                            {
                                case BlockType.Rock:
                                case BlockType.Dirt:
                                case BlockType.Mud:
                                case BlockType.Grass:
                                case BlockType.Sand:
                                case BlockType.DirtSign:
                                case BlockType.Ore:
                                case BlockType.SolidRed:
                                case BlockType.SolidBlue:
                                case BlockType.TransRed:
                                case BlockType.GlassR:
                                case BlockType.GlassB:
                                case BlockType.TransBlue:
                                case BlockType.Water:
                                case BlockType.Ladder:
                                case BlockType.Shock:
                                case BlockType.Jump:
                                case BlockType.Explosive:
                                case BlockType.Lava:
                                case BlockType.StealthBlockB:
                                case BlockType.StealthBlockR:
                                case BlockType.TrapR:
                                case BlockType.TrapB:
                                case BlockType.Road:
                               // case BlockType.RadarBlue:
                               // case BlockType.RadarRed:
                                    destroyBlock = true;
                                    break;
                            }
                            if (destroyBlock)
                            if (team != blockCreatorTeam[x+dx,y+dy,z+dz])
                            {
                                if (blockList[x + dx, y + dy, z + dz] == BlockType.RadarRed)//requires special remove
                                {//never executes??
                                    ConsoleWrite("blue should go off radar");
                                    foreach (Player p in playerList.Values)
                                    {
                                        if (p.Alive && p.Team == PlayerTeam.Blue)
                                        {
                                            if (p.Content[1] == 1)
                                            {
                                                ConsoleWrite("blue went off radar");
                                                p.Content[1] = 0;//goes off radar again
                                                SendPlayerContentUpdate(p, 1);
                                            }
                                        }
                                    }
                                }
                                else if (blockList[x + dx, y + dy, z + dz] == BlockType.RadarBlue)//requires special remove
                                {
                                    ConsoleWrite("red should go off radar");
                                    foreach (Player p in playerList.Values)
                                    {
                                        if (p.Alive && p.Team == PlayerTeam.Red)
                                        {
                                            if (p.Content[1] == 1)
                                            {
                                                ConsoleWrite("red went off radar");
                                                p.Content[1] = 0;//goes off radar again
                                                SendPlayerContentUpdate(p, 1);
                                            }
                                        }
                                    }
                                }

                                SetBlock((ushort)(x + dx), (ushort)(y + dy), (ushort)(z + dz), BlockType.None, PlayerTeam.None);
                            }
                        }
            }
            else
            {
                int radius = (int)Math.Ceiling((double)varGetI("explosionradius"));
                int size = radius * 2 + 1;
                int center = radius+1;
                //ConsoleWrite("Radius: " + radius + ", Size: " + size + ", Center: " + center);
                for (int dx = -center+1; dx < center; dx++)
                    for (int dy = -center+1; dy < center; dy++)
                        for (int dz = -center+1; dz < center; dz++)
                        {
                            if (tntExplosionPattern[dx+center-1, dy+center-1, dz+center-1]) //Warning, code duplication ahead!
                            {
                                // Check that this is a sane block position.
                                if (x + dx <= 0 || y + dy <= 0 || z + dz <= 0 || x + dx > MAPSIZE - 1 || y + dy > MAPSIZE - 1 || z + dz > MAPSIZE - 1)
                                    continue;

                                // Chain reactions!
                                if (blockList[x + dx, y + dy, z + dz] == BlockType.Explosive)
                                    DetonateAtPoint(x + dx, y + dy, z + dz);

                                // Detonation of normal blocks.
                                bool destroyBlock = false;
                                switch (blockList[x + dx, y + dy, z + dz])
                                {
                                    case BlockType.Rock:
                                    case BlockType.Dirt:
                                    case BlockType.Grass:
                                    case BlockType.Mud:
                                    case BlockType.Sand:
                                    case BlockType.DirtSign:
                                    case BlockType.Ore:
                                    case BlockType.SolidRed:
                                    case BlockType.SolidBlue:
                                    case BlockType.RadarRed:
                                    case BlockType.RadarBlue:
                                    case BlockType.TransRed:
                                    case BlockType.TransBlue:
                                    case BlockType.GlassR:
                                    case BlockType.GlassB:
                                    case BlockType.Water:
                                    case BlockType.Ladder:
                                    case BlockType.Shock:
                                    case BlockType.Jump:
                                    case BlockType.Explosive:
                                    case BlockType.Lava:
                                    case BlockType.Road:
                                    case BlockType.TrapR:
                                    case BlockType.TrapB:
                                    case BlockType.StealthBlockB:
                                    case BlockType.StealthBlockR:
                                        destroyBlock = true;
                                        break;
                                }
                                if (destroyBlock)
                                    SetBlock((ushort)(x + dx), (ushort)(y + dy), (ushort)(z + dz), BlockType.None, PlayerTeam.None);
                            }
                        }
            }
            ExplosionEffectAtPoint(x, y, z, 3, blockCreatorTeam[x, y, z]);
        }
        public void ArtifactTeamBonus(PlayerTeam team, int cc, bool state)
        {

            NetBuffer msgBuffer;
            string artmessage = "";

            if (state)
            {
                artifactActive[(byte)team, cc]++;
                msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);
                if (team == PlayerTeam.Red)
                    msgBuffer.Write((byte)ChatMessageType.SayRedTeam);
                else if (team == PlayerTeam.Blue)
                    msgBuffer.Write((byte)ChatMessageType.SayBlueTeam);
                
                artmessage = "We now possess the " + ArtifactInformation.GetName(cc);
            }
            else
            {
                artifactActive[(byte)team, cc]--;
                msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);
                if (team == PlayerTeam.Red)
                    msgBuffer.Write((byte)ChatMessageType.SayRedTeam);
                else if (team == PlayerTeam.Blue)
                    msgBuffer.Write((byte)ChatMessageType.SayBlueTeam);

                artmessage = "The " + ArtifactInformation.GetName(cc) + " has been lost";
            }

            switch(cc)
            {
                case 1://material artifact
                    if (state)
                    {
                        artmessage += ", regenerating team ore periodically";
                    }
                    else
                    {
                        artmessage += " reducing our periodic ore supply";
                    }
                    break;
                case 2://vampire artifact
                    if (state)
                    {
                        artmessage += ", giving our team minor life stealing attacks";
                    }
                    else
                    {
                        artmessage += " reducing our life stealing attacks";
                    }
                    break;
                case 3://regeneration artifact
                    if (state)
                    {
                        teamRegeneration[(byte)team] += 2;
                        artmessage += ", regenerating faster";
                    }
                    else
                    {
                        teamRegeneration[(byte)team] -= 2;
                        artmessage += " regenerating slower";
                    }
                    break;
           
                case 4://aqua
                    if (artifactActive[(byte)team, cc] < 1)
                    {
                        artmessage += " and we may no longer water breathe or dig underwater";
                        SendActiveArtifactUpdate(team, cc);
                    }
                    else if (artifactActive[(byte)team, cc] == 1)
                    {
                        artmessage += ", we may now breathe and dig underwater";
                        SendActiveArtifactUpdate(team, cc);
                    }
                    break;
                case 5://golden artifact
                    if (state)
                    {
                        artmessage += ", generating periodic gold deposits for our team";
                    }
                    else
                    {
                        artmessage += " reducing our periodic gold supplies";
                    }
                    break;
                case 6://storm artifact
                    if (state)
                    {
                        artmessage += ", granting immunity to any area effects caused by artifacts";
                    }
                    else
                    {
                        if(artifactActive[(byte)team, 6] == 0)
                        artmessage += " making us vulnerable to area effects caused by artifacts";
                    }
                    break;
                case 7://reflect artifact
                    if (state)
                    {
                        artmessage += ", causing our team blocks to reflect a small amount of damage";
                    }
                    else
                    {
                        if (artifactActive[(byte)team, 7] == 0)
                            artmessage += " removing our block damage reflection";
                    }
                    break;
                case 8://medical artifact
                    if (state)
                    {
                        artmessage += ", healing our teams ailments";
                    }
                    else
                    {
                        if (artifactActive[(byte)team, 8] == 0)
                            artmessage += " leaving us without ailment protection";
                    }
                    break;
                case 9://stone artifact
                    if (state)
                    {
                        artmessage += ", reducing any knockbacks";
                        SendActiveArtifactUpdate(team, cc);
                    }
                    else
                    {
                        if (artifactActive[(byte)team, cc] == 0)
                            artmessage += " leaving us without knockback protection";

                        SendActiveArtifactUpdate(team, cc);
                    }
                    break;
            }

            if (artmessage != "")
            {
                artmessage += "!";
                msgBuffer.Write(Defines.Sanitize(artmessage));
                foreach (NetConnection netConn in playerList.Keys)
                    if (netConn.Status == NetConnectionStatus.Connected && playerList[netConn].Team == team)
                        netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
            }
        }
        public void UseDetonator(Player player)
        {
            while (player.ExplosiveList.Count > 0)
            {
                Vector3 blockPos = player.ExplosiveList[0];
                ushort x = (ushort)blockPos.X;
                ushort y = (ushort)blockPos.Y;
                ushort z = (ushort)blockPos.Z;

                if (blockList[x, y, z] != BlockType.Explosive)
                    player.ExplosiveList.RemoveAt(0);
                else if (!varGetB("tnt"))
                {
                    player.ExplosiveList.RemoveAt(0);
                    ExplosionEffectAtPoint(x,y,z,3,player.Team);
                    // Remove the block that is detonating.
                    SetBlock(x, y, z, BlockType.None, PlayerTeam.None);
                }
                else
                    DetonateAtPoint(x, y, z);
            }
        }

        public void UseRemote(Player player)
        {
            if (player.Content[5] > 0)
            {
                PlayerInteract(player, (uint)(player.Content[5]), (uint)(player.Content[6]), (uint)(player.Content[7]), (uint)(player.Content[8]));
            }
            else
            {
                SendServerMessageToPlayer("Remote is not attached to anything.", player.NetConn);
            }
        }

        public void Hide(Player player)
        {
            if (player.Class == PlayerClass.Prospector && player.Content[5] == 0 && player.Content[6] > 3)
            {
                player.Content[1] = 0;
                SendPlayerContentUpdate(player, 1);
                player.Content[5] = 1;//no more sight
                SendContentSpecificUpdate(player, 5);
                SendPlayerContentUpdate(player, 5);
                SendServerMessageToPlayer("You are now hidden!", player.NetConn);

                EffectAtPoint(player.Position - Vector3.UnitY * 1.5f, 1);
            }
            else if (player.Class == PlayerClass.Prospector && player.Content[5] == 1)
            {
                return;//unhiding is disabled for now
                //player.Content[1] = 0;//reappear on radar
                //SendPlayerContentUpdate(player, 1);
                //player.Content[5] = 0;//sight
                //SendContentSpecificUpdate(player, 5);
                //SendPlayerContentUpdate(player, 5);
                //SendServerMessageToPlayer("You have unhidden!", player.NetConn);
                //EffectAtPoint(player.Position - Vector3.UnitY * 1.5f, 1);
            }

        }

        public void SetRemote(Player player)
        {
            player.Content[2] = (int)player.Position.X;
            player.Content[3] = (int)player.Position.Y;
            player.Content[4] = (int)player.Position.Z;
            player.Content[5] = 0;
            player.Content[9] = 1;
            SendServerMessageToPlayer("You are now linking an object to the remote.", player.NetConn);
        }

        public void SetRemote(Player player, uint btn, uint x, uint y, uint z)
        {
                if(x > 0 && x < MAPSIZE - 1 && y > 0 && y < MAPSIZE - 1 && z > 0 && z < MAPSIZE - 1)
                {
                    player.Content[5] = (int)btn;
                    player.Content[6] = (int)x;
                    player.Content[7] = (int)y;
                    player.Content[8] = (int)z;
                    SendServerMessageToPlayer("linked remote to action " + btn + " on " + blockList[x, y, z] + ".", player.NetConn);
               }
        }
        public bool HingeBlockTypes(BlockType block, PlayerTeam team)
        {
            switch (block)
            {
                case BlockType.SolidRed:
                case BlockType.SolidRed2:
                    if (team == PlayerTeam.Red)
                        return true;
                    break;
                case BlockType.SolidBlue2:
                case BlockType.SolidBlue:
                    if (team == PlayerTeam.Blue)
                    return true;
                    break;
                default:
                    break;
            }
            return false;
        }
        public bool HingeBlockTypes(BlockType block)
        {
            switch (block)
            {
                case BlockType.SolidRed:
                case BlockType.SolidRed2:
                case BlockType.SolidBlue2:
                case BlockType.SolidBlue:
                        return true;
                    break;
                default:
                    break;
            }
            return false;
        }
        public bool Trigger(int x, int y, int z, int ox, int oy, int oz, int btn, Player player)
        {
            //if object can be manipulated by levers, it should always return true if the link should remain persistent
            //if the Trigger function returns false, it will remove the link
            if (player != null)
            if (player.Content[2] > 0)//player is attempting to link something
            {
                if (player.Content[9] == 1 && player.Class == PlayerClass.Engineer)
                {
                    if (x > 0 && x < MAPSIZE - 1 && y > 0 && y < MAPSIZE - 1 && z > 0 && z < MAPSIZE - 1)
                    {
                        if (blockList[x, y, z] == BlockType.ResearchB || blockList[x, y, z] == BlockType.ResearchR || blockList[x, y, z] == BlockType.BaseBlue || blockList[x, y, z] == BlockType.BaseRed || blockList[x, y, z] == BlockType.ArtCaseR || blockList[x, y, z] == BlockType.ArtCaseB || blockList[x, y, z] == BlockType.BankRed || blockList[x, y, z] == BlockType.BankBlue)
                        {
                            player.Content[2] = 0;
                            player.Content[3] = 0;
                            player.Content[4] = 0;
                            player.Content[5] = 0;
                            player.Content[9] = 0;
                            SendServerMessageToPlayer("The remote cannot function on " + blockList[x, y, z] + ".", player.NetConn);                           
                        }
                        else
                        {
                            player.Content[5] = (int)btn;
                            player.Content[6] = (int)x;
                            player.Content[7] = (int)y;
                            player.Content[8] = (int)z;
                            player.Content[2] = 0;
                            player.Content[3] = 0;
                            player.Content[4] = 0;
                            //player.Content[5] = 0;
                            SendServerMessageToPlayer("Linked remote to action " + btn + " on " + blockList[x, y, z] + ".", player.NetConn);
                            player.Content[9] = 0;
                        }
                        return true;
                    }
                }
                if (x == player.Content[2] && y == player.Content[3] && z == player.Content[4])
                {
                    player.Content[2] = 0;
                    player.Content[3] = 0;
                    player.Content[4] = 0;
                    SendContentSpecificUpdate(player, 2);
                    SendContentSpecificUpdate(player, 3);
                    SendContentSpecificUpdate(player, 4);
                    SendServerMessageToPlayer("Cancelled link.", player.NetConn);
                    return true;
                }

                int freeslot = 9;
                int nb = 0;
                for (nb = 2; nb < 7; nb++)
                {
                    if (blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6 + 1] == x && blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6 + 2] == y && blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6 + 3] == z)
                    {


                        blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6] = 0;
                        blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6 + 1] = 0;
                        blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6 + 2] = 0;
                        blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6 + 3] = 0;
                        blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6 + 4] = 0;//unlinked

                        player.Content[2] = 0;
                        player.Content[3] = 0;
                        player.Content[4] = 0;
                        SendContentSpecificUpdate(player, 2);
                        SendContentSpecificUpdate(player, 3);
                        SendContentSpecificUpdate(player, 4);

                        SendServerMessageToPlayer(blockList[x, y, z] + " was unlinked.", player.NetConn);

                        return true;
                    }
                    else if (blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6] == 0 && freeslot == 9)
                    {
                        freeslot = nb;
                        break;//makes sure that we arent reattaching links over and over
                    }
                }

                if (freeslot == 9)
                    return false;

                if (nb != 7)//didnt hit end of switch-link limit
                {//should check teams and connection to itself
                    //range check

                    if (Distf(new Vector3(x, y, z), new Vector3(player.Content[2], player.Content[3], player.Content[4])) < 10)
                    {
                        //Vector3 heading = new Vector3(player.Content[2], player.Content[3], player.Content[4]);
                        //heading -= new Vector3(x, y, z);
                        //heading.Normalize();
                        //if (RayCollision(new Vector3(x, y, z) + heading * 0.4f, heading, (float)(Distf(new Vector3(x, y, z), new Vector3(player.Content[2], player.Content[3], player.Content[4]))), 10, blockList[x, y, z]))
                        //{
                            blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6 + 1] = (int)(x);
                            blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6 + 2] = (int)(y);
                            blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6 + 3] = (int)(z);
                            blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6 + 4] = (int)(btn);
                            blockListContent[player.Content[2], player.Content[3], player.Content[4], nb * 6] = 100;
                            SendServerMessageToPlayer(blockList[player.Content[2], player.Content[3], player.Content[4]] + " linked action " + btn + " on " + blockList[x, y, z] + ".", player.NetConn);
                        //}
                        //else
                        //{
                        //    SendServerMessageToPlayer(blockList[player.Content[2], player.Content[3], player.Content[4]] + " was not in line of sight of " + blockList[x, y, z] + " to link!", player.NetConn);
                        //}
                    }
                    else
                    {
                        SendServerMessageToPlayer(blockList[player.Content[2], player.Content[3], player.Content[4]] + " was too far away from the " + blockList[x, y, z] + " to link!", player.NetConn);
                    }
                    player.Content[2] = 0;
                    player.Content[3] = 0;
                    player.Content[4] = 0;
                    SendContentSpecificUpdate(player, 2);
                    SendContentSpecificUpdate(player, 3);
                    SendContentSpecificUpdate(player, 4);
                }
                else
                {
                    SendServerMessageToPlayer("Lever is too overloaded to link more objects.", player.NetConn);
                    player.Content[2] = 0;
                    player.Content[3] = 0;
                    player.Content[4] = 0;
                    SendContentSpecificUpdate(player, 2);
                    SendContentSpecificUpdate(player, 3);
                    SendContentSpecificUpdate(player, 4);
                }
                return true;
            }

            //beginning of trigger actions
            if (blockList[x, y, z] == BlockType.Pipe)
            {
                ConsoleWrite("Chain connected to src:" + blockListContent[x, y, z, 1] + " src: " + blockListContent[x, y, z, 2] + " dest: " + blockListContent[x, y, z, 4] + " Connections: " + blockListContent[x, y, z, 3]);
            }
            else if (blockList[x, y, z] == BlockType.BeaconRed || blockList[x, y, z] == BlockType.BeaconBlue)
            {
                if (player != null)
                {
                    if (btn == 1)
                    {
                        if (player.Team == PlayerTeam.Red && blockList[x, y, z] == BlockType.BeaconRed || player.Team == PlayerTeam.Blue && blockList[x, y, z] == BlockType.BeaconBlue)
                        {
                            
                        }
                    }
                }
            }
            else if (blockList[x, y, z] == BlockType.ResearchR || blockList[x, y, z] == BlockType.ResearchB)
            {
                if (player != null)
                {
                    if (btn == 1)
                    {
                        if (player.Team == PlayerTeam.Red && blockList[x, y, z] == BlockType.ResearchR || player.Team == PlayerTeam.Blue && blockList[x, y, z] == BlockType.ResearchB)
                        {
                            if (blockListContent[x, y, z, 0] == 0)
                            {
                                blockListContent[x, y, z, 0]++;
                                SendServerMessageToPlayer("Research has begun.", player.NetConn);
                              
                            }
                            else
                            {
                                blockListContent[x, y, z, 0]--;
                                SendServerMessageToPlayer("Research paused.", player.NetConn);
                            }
                        }
                    }
                    else if (btn == 2)
                    {
                        if (player.Team == PlayerTeam.Red && blockList[x, y, z] == BlockType.ResearchR || player.Team == PlayerTeam.Blue && blockList[x, y, z] == BlockType.ResearchB)
                        {
                            if (blockListContent[x, y, z, 1] < (byte)Research.MAXIMUM-1)
                            {
                                blockListContent[x, y, z, 0] = 0;
                                blockListContent[x, y, z, 1]++;
                                SendServerMessageToPlayer("Research topic:" + ResearchInformation.GetName((Research)blockListContent[x, y, z, 1]) + "(" + ResearchComplete[(byte)player.Team, blockListContent[x, y, z, 1]] + ") (" + ResearchProgress[(byte)player.Team, blockListContent[x, y, z, 1]] + " gold)", player.NetConn);
                               // blockListContent[x, y, z, 2] = ResearchInformation.GetCost((Research)blockListContent[x, y, z, 1]);
                            }
                            else
                            {
                                blockListContent[x, y, z, 0] = 0;
                                blockListContent[x, y, z, 1] = 1;
                                SendServerMessageToPlayer("Research topic:" + ResearchInformation.GetName((Research)blockListContent[x, y, z, 1]) + "(" + ResearchComplete[(byte)player.Team, blockListContent[x, y, z, 1]] + ") (" + ResearchProgress[(byte)player.Team, blockListContent[x, y, z, 1]] + " gold)", player.NetConn);
                            }
                        }
                    }
                }
            }
            else if (blockList[x, y, z] == BlockType.ArtCaseR || blockList[x, y, z] == BlockType.ArtCaseB)
            {
                if (player != null)
                {
                    if (btn == 1)
                    {
                        if (player.Team == PlayerTeam.Red && blockList[x, y, z] == BlockType.ArtCaseR || player.Team == PlayerTeam.Blue && blockList[x, y, z] == BlockType.ArtCaseB)
                            if (player.Content[10] > 0 && blockListContent[x, y, z, 6] == 0)
                            {//place artifact
                                uint arty = SetItem(ItemType.Artifact, new Vector3(x + 0.5f, y + 1.5f, z + 0.5f), Vector3.Zero, Vector3.Zero, player.Team, player.Content[10]);
                                itemList[arty].Content[6] = 1;//lock artifact in place
                                blockListContent[x, y, z, 6] = (int)(arty);
                                player.Content[10] = 0;
                                SendItemContentSpecificUpdate(itemList[arty], 6);//lock item
                                SendContentSpecificUpdate(player, 10);//inform players
                                SendPlayerContentUpdate(player, 10);//inform activator

                                ArtifactTeamBonus(player.Team, itemList[arty].Content[10], true);

                                if (blockList[x, y, z] == BlockType.ArtCaseB)
                                {
                                    teamArtifactsBlue++;
                                    SendScoreUpdate();
                                }
                                else if (blockList[x, y, z] == BlockType.ArtCaseR)
                                {
                                    teamArtifactsRed++;
                                    SendScoreUpdate();
                                }
                            }
                    }
                    else if (btn == 2)
                    {
                        if (player.Team == PlayerTeam.Red && blockList[x, y, z] == BlockType.ArtCaseR || player.Team == PlayerTeam.Blue && blockList[x, y, z] == BlockType.ArtCaseB)
                            if (player.Content[10] == 0 && blockListContent[x, y, z, 6] > 0)
                            {//retrieve artifact
                                uint arty = (uint)(blockListContent[x, y, z, 6]);
                                itemList[arty].Content[6] = 0;//unlock artifact in place
                                blockListContent[x, y, z, 6] = 0;//artcase empty
                                player.Content[10] = itemList[arty].Content[10];//player is holding the new artifact
                                itemList[arty].Disposing = true;//item gets removed

                                SendContentSpecificUpdate(player, 10);//inform players
                                SendPlayerContentUpdate(player, 10);//inform activator

                                ArtifactTeamBonus(player.Team, itemList[arty].Content[10], false);
                                
                                if (blockList[x, y, z] == BlockType.ArtCaseB)
                                {
                                    teamArtifactsBlue--;
                                    SendScoreUpdate();
                                }
                                else if (blockList[x, y, z] == BlockType.ArtCaseR)
                                {
                                    teamArtifactsRed--;
                                    SendScoreUpdate();
                                }
                            }
                    }
                }
                return true;
            }
            else if (blockList[x, y, z] == BlockType.BaseBlue || blockList[x, y, z] == BlockType.BaseRed)
            {
                if (player != null)
                {
                    if (btn == 1)
                    {
                        if (player.Team == PlayerTeam.Red && blockList[x, y, z] == BlockType.BaseRed || player.Team == PlayerTeam.Blue && blockList[x, y, z] == BlockType.BaseBlue)
                            //if (player.Content[11] > 0 && blockListContent[x, y, z, 1] == 0)
                            {//begin forge
                              //  player.Content[11]--;
                              //  player.Weight--;
                              //requirement turned off for now

                                SendWeightUpdate(player);
                                SendContentSpecificUpdate(player, 11);

                                blockListContent[x, y, z, 1] = artifactCost;
                                NetBuffer msgBuffer = netServer.CreateBuffer();
                                msgBuffer = netServer.CreateBuffer();
                                msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);

                                if (player.Team == PlayerTeam.Red)
                                    msgBuffer.Write((byte)ChatMessageType.SayRedTeam);
                                else if (player.Team == PlayerTeam.Blue)
                                    msgBuffer.Write((byte)ChatMessageType.SayBlueTeam);

                                msgBuffer.Write(Defines.Sanitize("The " + player.Team + " has begun forging an artifact!"));

                                foreach (NetConnection netConn in playerList.Keys)
                                    if (netConn.Status == NetConnectionStatus.Connected)
                                        netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
                            }
                    }
                    else if (btn == 2)
                    {

                    }
                }
                return true;
            }
            else if (blockList[x, y, z] == BlockType.Lever)
            {
                if (btn == 1)
                {
                    if (player != null)
                        SendServerMessageToPlayer("You pull the lever!", player.NetConn);

                    if (blockListContent[x, y, z, 0] == 0)//not falling
                    {
                        for (int a = 2; a < 7; a++)
                        {
                            if (blockListContent[x, y, z, a * 6] > 0)
                            {
                                int bx = blockListContent[x, y, z, a * 6 + 1];
                                int by = blockListContent[x, y, z, a * 6 + 2];
                                int bz = blockListContent[x, y, z, a * 6 + 3];
                                int bbtn = blockListContent[x, y, z, a * 6 + 4];

                                if (Trigger(bx, by, bz, x, y, z, bbtn, null) == false)
                                {
                                    //trigger returned no result, delete the link
                                    blockListContent[x, y, z, a * 6] = 0;
                                    blockListContent[x, y, z, a * 6 + 1] = 0;
                                    blockListContent[x, y, z, a * 6 + 2] = 0;
                                    blockListContent[x, y, z, a * 6 + 3] = 0;
                                    blockListContent[x, y, z, a * 6 + 4] = 0;
                                }
                            }
                        }
                    }

                }
                else if (btn == 2)
                {
                    if (player != null)//only a player can invoke this action
                    {
                        int nb = 0;
                        for (nb = 2; nb < 7; nb++)
                        {
                            if (blockListContent[x, y, z, nb * 6] == 0)
                            {
                                break;
                            }
                        }

                        if (nb != 7)//didnt hit end of switch-link limit
                        {

                            SendServerMessageToPlayer("You are now linking objects.", player.NetConn);

                            player.Content[2] = (int)(x);//player is creating a link to this switch
                            player.Content[3] = (int)(y);
                            player.Content[4] = (int)(z);
                            SendContentSpecificUpdate(player, 2);
                            SendContentSpecificUpdate(player, 3);
                            SendContentSpecificUpdate(player, 4);
                        }
                        else
                        {
                            SendServerMessageToPlayer("This lever is overloaded, you are now unlinking objects.", player.NetConn);

                            player.Content[2] = (int)(x);//player is creating a link to this switch
                            player.Content[3] = (int)(y);
                            player.Content[4] = (int)(z);
                            SendContentSpecificUpdate(player, 2);
                            SendContentSpecificUpdate(player, 3);
                            SendContentSpecificUpdate(player, 4);

                        }
                    }
                }
                return true;
            }
            else if (blockList[x, y, z] == BlockType.Plate)
            {
                if (btn == 1)
                {
                    // if (player != null)
                    //   SendServerMessageToPlayer("You stand on a pressure plate!", player.NetConn);

                    if (blockListContent[x, y, z, 0] == 0 && blockListContent[x, y, z, 1] < 1)//not falling and recharged
                    {
                        if (player != null)
                            blockListContent[x, y, z, 1] = blockListContent[x, y, z, 2];//only players will trigger the timer

                        for (int a = 2; a < 7; a++)
                        {
                            if (blockListContent[x, y, z, a * 6] > 0)
                            {
                                int bx = blockListContent[x, y, z, a * 6 + 1];
                                int by = blockListContent[x, y, z, a * 6 + 2];
                                int bz = blockListContent[x, y, z, a * 6 + 3];
                                int bbtn = blockListContent[x, y, z, a * 6 + 4];

                                if (Trigger(bx, by, bz, x, y, z, bbtn, null) == false)
                                {
                                    //trigger returned no result, delete the link
                                    blockListContent[x, y, z, a * 6] = 0;
                                    blockListContent[x, y, z, a * 6 + 1] = 0;
                                    blockListContent[x, y, z, a * 6 + 2] = 0;
                                    blockListContent[x, y, z, a * 6 + 3] = 0;
                                    blockListContent[x, y, z, a * 6 + 4] = 0;
                                }
                            }
                        }
                    }

                }
                else if (btn == 2)
                {
                    if (player != null)//only a player can invoke this action
                    {
                        int nb = 0;
                        for (nb = 2; nb < 7; nb++)
                        {
                            if (blockListContent[x, y, z, nb * 6] == 0)
                            {
                                break;
                            }
                        }

                        if (nb != 7)//didnt hit end of switch-link limit
                        {

                            SendServerMessageToPlayer("You are now linking objects.", player.NetConn);

                            player.Content[2] = (int)(x);//player is creating a link to this switch
                            player.Content[3] = (int)(y);
                            player.Content[4] = (int)(z);
                            SendContentSpecificUpdate(player, 2);
                            SendContentSpecificUpdate(player, 3);
                            SendContentSpecificUpdate(player, 4);
                        }
                        else
                        {
                            SendServerMessageToPlayer("This lever is overloaded, you are now unlinking objects.", player.NetConn);

                            player.Content[2] = (int)(x);//player is creating a link to this switch
                            player.Content[3] = (int)(y);
                            player.Content[4] = (int)(z);
                            SendContentSpecificUpdate(player, 2);
                            SendContentSpecificUpdate(player, 3);
                            SendContentSpecificUpdate(player, 4);

                        }
                    }
                }
                else if (btn == 3)
                {
                    if (blockListContent[x, y, z, 2] > 1)
                    {
                        blockListContent[x, y, z, 2] -= 1;//decrease retrigger timer
                        if (player != null)
                            SendServerMessageToPlayer("The pressure plate retrigger decreased to " + (blockListContent[x, y, z, 2] * 400) + " milliseconds.", player.NetConn);
                    }
                    else
                    {
                        blockListContent[x, y, z, 2] = 0;
                        SendServerMessageToPlayer("The pressure plate now only retriggers when touched.", player.NetConn);
                    }
                }
                else if (btn == 4)
                {
                    blockListContent[x, y, z, 2] += 1;//increase retrigger timer
                    if (player != null)
                        SendServerMessageToPlayer("The pressure plate retrigger increased to " + (blockListContent[x, y, z, 2] * 400) + " milliseconds.", player.NetConn);
                }
                return true;
            }
            else if (blockList[x, y, z] == BlockType.Pump)
            {
                if (btn == 1)
                {
                    if (blockListContent[x, y, z, 0] == 0)
                    {

                        if (player != null)
                            SendServerMessageToPlayer(blockList[x, y, z] + " activated.", player.NetConn);

                        blockListContent[x, y, z, 0] = 1;
                    }
                    else
                    {
                        if (player != null)
                            SendServerMessageToPlayer(blockList[x, y, z] + " deactivated.", player.NetConn);

                        blockListContent[x, y, z, 0] = 0;
                    }
                }
                else if (btn == 2)
                {
                    if (blockListContent[x, y, z, 1] < 5)//rotate
                    {
                        blockListContent[x, y, z, 1] += 1;

                        if (player != null)
                            SendServerMessageToPlayer(blockList[x, y, z] + " rotated to " + blockListContent[x, y, z, 1], player.NetConn);

                        if (blockListContent[x, y, z, 1] == 1)
                        {
                            blockListContent[x, y, z, 2] = 0;//x input
                            blockListContent[x, y, z, 3] = -1;//y input
                            blockListContent[x, y, z, 4] = 0;//z input
                            blockListContent[x, y, z, 5] = 1;//x output
                            blockListContent[x, y, z, 6] = 0;//y output
                            blockListContent[x, y, z, 7] = 0;//z output
                            //pulls from below, pumps to side
                        }
                        else if (blockListContent[x, y, z, 1] == 2)
                        {
                            blockListContent[x, y, z, 2] = 0;//x input
                            blockListContent[x, y, z, 3] = -1;//y input
                            blockListContent[x, y, z, 4] = 0;//z input
                            blockListContent[x, y, z, 5] = -1;//x output
                            blockListContent[x, y, z, 6] = 0;//y output
                            blockListContent[x, y, z, 7] = 0;//z output
                            //pulls from below, pumps to otherside
                        }
                        else if (blockListContent[x, y, z, 1] == 3)
                        {
                            blockListContent[x, y, z, 2] = 0;//x input
                            blockListContent[x, y, z, 3] = -1;//y input
                            blockListContent[x, y, z, 4] = 0;//z input
                            blockListContent[x, y, z, 5] = 0;//x output
                            blockListContent[x, y, z, 6] = 0;//y output
                            blockListContent[x, y, z, 7] = 1;//z output
                            //pulls from below, pumps to otherside
                        }
                        else if (blockListContent[x, y, z, 1] == 4)
                        {
                            blockListContent[x, y, z, 2] = 0;//x input
                            blockListContent[x, y, z, 3] = -1;//y input
                            blockListContent[x, y, z, 4] = 0;//z input
                            blockListContent[x, y, z, 5] = 0;//x output
                            blockListContent[x, y, z, 6] = 0;//y output
                            blockListContent[x, y, z, 7] = -1;//z output
                            //pulls from below, pumps to otherside
                        }
                    }
                    else
                    {
                        blockListContent[x, y, z, 1] = 0;//reset rotation

                        if (player != null)
                            SendServerMessageToPlayer(blockList[x, y, z] + " rotated to " + blockListContent[x, y, z, 1], player.NetConn);

                        blockListContent[x, y, z, 2] = 0;//x input
                        blockListContent[x, y, z, 3] = -1;//y input
                        blockListContent[x, y, z, 4] = 0;//z input
                        blockListContent[x, y, z, 5] = 0;//x output
                        blockListContent[x, y, z, 6] = 1;//y output
                        blockListContent[x, y, z, 7] = 0;//z output
                        //pulls from below, pumps straight up
                    }
                }
                return true;
            }
            else if (blockList[x, y, z] == BlockType.Barrel)
            {
                if (btn == 1)
                {
                    if (blockListContent[x, y, z, 0] == 0)
                    {
                        if (player != null)
                            SendServerMessageToPlayer("Filling..", player.NetConn);

                        blockListContent[x, y, z, 0] = 1;
                    }
                    else
                    {
                        if (player != null)
                            SendServerMessageToPlayer("Emptying..", player.NetConn);

                        blockListContent[x, y, z, 0] = 0;
                    }
                }
                else if (btn == 2)
                {
                  
                }
                return true;
            }
            else if (blockList[x, y, z] == BlockType.Hinge)
            {
                if (btn == 1)
                {
                    bool repairme = false;

                    if (player != null)
                        SendServerMessageToPlayer("You attempt to work the hinge!", player.NetConn);

                    if (blockListContent[x, y, z, 0] == 0)//not falling
                    {
                        bool green = true;
                        //blockListContent[x, y, z, 2] = 0;
                        // blockListContent[x, y, z, 2] = 0;// +x
                        // blockListContent[x, y, z, 2] = 2;// -x

                        for (int a = 2; a < 7; a++)
                        {
                            //if (blockList[blockListContent[x, y, z, a * 6 + 1], blockListContent[x, y, z, a * 6 + 2], blockListContent[x, y, z, a * 6 + 3]] == (BlockType)(blockListContent[x, y, z, a * 6]))
                            //{
                            if (HingeBlockTypes(blockList[blockListContent[x, y, z, a * 6 + 1], blockListContent[x, y, z, a * 6 + 2], blockListContent[x, y, z, a * 6 + 3]]))
                            {
                            }
                            else
                            {
                                blockListContent[x, y, z, a * 6] = 0;
                                blockListContent[x, y, z, a * 6 + 1] = 0;
                                blockListContent[x, y, z, a * 6 + 2] = 0;
                                blockListContent[x, y, z, a * 6 + 3] = 0;
                                break;
                            }
                            if (blockListContent[x, y, z, a * 6] > 0)
                            {
                                int bx = blockListContent[x, y, z, a * 6 + 1];
                                int by = blockListContent[x, y, z, a * 6 + 2];
                                int bz = blockListContent[x, y, z, a * 6 + 3];
                                int relx = bx - x;
                                int rely = by - y;
                                int relz = bz - z;

                                BlockType block = BlockType.Pipe;
                                int mod = 1;

                                if (blockListContent[x, y, z, 2] == 2 || blockListContent[x, y, z, 2] == 4)//-x & -z
                                {
                                    mod = -1;
                                }

                                if (blockListContent[x, y, z, 1] == 1 && blockListContent[x, y, z, 2] < 3)//+x -> +y//checking upwards clear
                                {
                                    //if (player != null)
                                    //SendServerMessageToPlayer("gc +-x +y", player.NetConn);
                                    //diagonal block = blockList[x + rely, y + relx, z + relz];//x + rely * (a - 1), y + relx * (a - 1), z + relz * (a - 1)];
                                    block = blockList[x, y + (relx * mod), z];//relx*mod
                                }
                                else if (blockListContent[x, y, z, 1] == 1 && blockListContent[x, y, z, 2] > 2)//+z -> +y//checking upwards clear
                                {
                                    //if (player != null)
                                    //SendServerMessageToPlayer("gc +-z +y", player.NetConn);
                                    block = blockList[x, y + (relz * mod), z];//relx*mod
                                }
                                else if (blockListContent[x, y, z, 1] == 0 && blockListContent[x, y, z, 2] == 0)//+y -> +x
                                {
                                    //if (player != null)
                                    //SendServerMessageToPlayer("gc +y +x",player.NetConn);
                                    block = blockList[x + rely, y, z];
                                }
                                else if (blockListContent[x, y, z, 1] == 0 && blockListContent[x, y, z, 2] == 2)//+y -> -x
                                {
                                    //if (player != null)
                                    //SendServerMessageToPlayer("gc +y -x", player.NetConn);
                                    block = blockList[x - rely, y, z];
                                }
                                else if (blockListContent[x, y, z, 1] == 0 && blockListContent[x, y, z, 2] == 3)//+y -> +z
                                {
                                    //if (player != null)
                                    //SendServerMessageToPlayer("gc +y +z", player.NetConn);
                                    block = blockList[x, y, z + rely];
                                }
                                else if (blockListContent[x, y, z, 1] == 0 && blockListContent[x, y, z, 2] == 4)//+y -> -z
                                {
                                    //if (player != null)
                                    //SendServerMessageToPlayer("gc +y -z", player.NetConn);

                                    block = blockList[x, y, z - rely];
                                }
                                if (block != BlockType.None && block != BlockType.Water && block != BlockType.Lava)
                                {
                                    green = false;//obstruction

                                    //if (player != null)
                                    //{
                                    //    if (blockListContent[x, y, z, 1] != 1 && blockListContent[x, y, z, 2] == 0)
                                    //        SendServerMessageToPlayer("not clear +x +y:" + (a - 1) + " " + blockList[x, y + (relx * mod), z], player.NetConn);
                                    //    if (blockListContent[x, y, z, 1] != 1 && blockListContent[x, y, z, 2] == 2)
                                    //        SendServerMessageToPlayer("not clear -x +y:" + (a - 1) + " " + blockList[x, y + (relx * mod), z], player.NetConn);
                                    //    else if (blockListContent[x, y, z, 2] == 3)
                                    //        SendServerMessageToPlayer("not clear y+z:" + (a - 1) + " " + blockList[x, y, z + rely], player.NetConn);
                                    //    else if (blockListContent[x, y, z, 2] == 4)
                                    //        SendServerMessageToPlayer("not clear y-z:" + (a - 1) + " " + blockList[x, y, z - rely], player.NetConn);
                                    //    else if (blockListContent[x, y, z, 2] == 2)
                                    //        SendServerMessageToPlayer("not clear y-x:" + (a - 1) + " " + blockList[x-rely, y, z], player.NetConn);
                                    //    else
                                    //        SendServerMessageToPlayer("not clear y+x:" + (a - 1) + " " + blockList[x+rely, y, z], player.NetConn);
                                    //}

                                }
                            }
                        }

                        if (repairme == false)
                        {
                        }
                        else
                        {
                            if (player != null)
                                SendServerMessageToPlayer("Hinge requires repair.", player.NetConn);
                        }

                        if (repairme == false)
                            if (green == true)
                            {
                                for (int a = 2; a < 7; a++)//7
                                {
                                    if (blockListContent[x, y, z, a * 6] > 0)
                                    {
                                        if (repairme == false)
                                            if (HingeBlockTypes(blockList[blockListContent[x, y, z, a * 6 + 1], blockListContent[x, y, z, a * 6 + 2], blockListContent[x, y, z, a * 6 + 3]]))
                                            {
                                                int bx = blockListContent[x, y, z, a * 6 + 1];//data of block about to move
                                                int by = blockListContent[x, y, z, a * 6 + 2];
                                                int bz = blockListContent[x, y, z, a * 6 + 3];

                                                int relx = 0;
                                                int rely = 0;
                                                int relz = 0;

                                                if (blockListContent[x, y, z, 1] == 1 && blockListContent[x, y, z, 2] == 0)// +x -> +y
                                                {
                                                    relx = bx - x;
                                                    rely = 0;
                                                    relz = 0;

                                                    SetBlock((ushort)(x), (ushort)(by + relx), (ushort)(z), (BlockType)(blockListContent[x, y, z, a * 6]), blockCreatorTeam[bx, by, bz]);
                                                    blockListHP[x, by + relx, z] = blockListHP[bx, by, bz];

                                                    blockListContent[x, y, z, a * 6] = (byte)blockList[x, y + a - 1, z];
                                                    blockListContent[x, y, z, a * 6 + 1] = x;
                                                    blockListContent[x, y, z, a * 6 + 2] = y + a - 1;
                                                    blockListContent[x, y, z, a * 6 + 3] = z;
                                                    //if (player != null)
                                                    //SendServerMessageToPlayer("green +x +y", player.NetConn);
                                                }
                                                else if (blockListContent[x, y, z, 1] == 1 && blockListContent[x, y, z, 2] == 3)// +z -> +y
                                                {
                                                    relx = 0;
                                                    rely = 0;
                                                    relz = bz - z;

                                                    SetBlock((ushort)(x), (ushort)(by + relz), (ushort)(z), (BlockType)(blockListContent[x, y, z, a * 6]), blockCreatorTeam[bx, by, bz]);
                                                    blockListHP[x, by + relz, z] = blockListHP[bx, by, bz];

                                                    blockListContent[x, y, z, a * 6] = (byte)blockList[x, y + a - 1, z];
                                                    blockListContent[x, y, z, a * 6 + 1] = x;
                                                    blockListContent[x, y, z, a * 6 + 2] = y + a - 1;
                                                    blockListContent[x, y, z, a * 6 + 3] = z;
                                                    //if (player != null)
                                                    //SendServerMessageToPlayer("green +z +y", player.NetConn);
                                                }
                                                else if (blockListContent[x, y, z, 1] == 1 && blockListContent[x, y, z, 2] == 2)// -x -> +y
                                                {
                                                    relx = bx - x;
                                                    rely = 0;
                                                    relz = 0;

                                                    SetBlock((ushort)(x), (ushort)(by - relx), (ushort)(z), (BlockType)(blockListContent[x, y, z, a * 6]), blockCreatorTeam[bx, by, bz]);
                                                    blockListHP[x, by - relx, z] = blockListHP[bx, by, bz];

                                                    blockListContent[x, y, z, a * 6] = (byte)blockList[x, y + (a - 1), z];
                                                    blockListContent[x, y, z, a * 6 + 1] = x;
                                                    blockListContent[x, y, z, a * 6 + 2] = y + (a - 1);
                                                    blockListContent[x, y, z, a * 6 + 3] = z;
                                                    //if (player != null)
                                                    //SendServerMessageToPlayer("green -x +y", player.NetConn);
                                                }
                                                else if (blockListContent[x, y, z, 1] == 1 && blockListContent[x, y, z, 2] == 4)// -z -> +y
                                                {
                                                    relx = 0;
                                                    rely = 0;
                                                    relz = bz - z;

                                                    SetBlock((ushort)(x), (ushort)(by - relz), (ushort)(z), (BlockType)(blockListContent[x, y, z, a * 6]), blockCreatorTeam[bx, by, bz]);
                                                    blockListHP[x, by - relz, z] = blockListHP[bx, by, bz];

                                                    blockListContent[x, y, z, a * 6] = (byte)blockList[x, y + (a - 1), z];
                                                    blockListContent[x, y, z, a * 6 + 1] = x;
                                                    blockListContent[x, y, z, a * 6 + 2] = y + (a - 1);
                                                    blockListContent[x, y, z, a * 6 + 3] = z;
                                                    //if (player != null)
                                                    //SendServerMessageToPlayer("green -z +y", player.NetConn);
                                                }
                                                else if (blockListContent[x, y, z, 1] == 0 && blockListContent[x, y, z, 2] == 0)// +y -> +x
                                                {
                                                    relx = 0;
                                                    rely = by - y;
                                                    relz = 0;

                                                    if (blockList[bx + rely, y, z] == BlockType.Water || blockList[bx + rely, y, z] == BlockType.Lava)
                                                    {//water in our way
                                                        if (blockList[bx + rely, y + 1, z] == BlockType.None)
                                                        {//push water up one
                                                            SetBlock((ushort)(bx + rely), (ushort)(y + 1), (ushort)(z), blockList[bx + rely, y, z], PlayerTeam.None);
                                                            blockListContent[bx + rely, y + 1, z, 1] = blockListContent[bx + rely, y, z, 1];//copy temperature
                                                            blockListContent[bx + rely, y + 1, z, 2] = blockListContent[bx + rely, y, z, 2];//copy blocks future type
                                                        }
                                                    }

                                                    SetBlock((ushort)(bx + rely), (ushort)(y), (ushort)(z), (BlockType)(blockListContent[x, y, z, a * 6]), blockCreatorTeam[bx, by, bz]);
                                                    blockListHP[bx + rely, y, z] = blockListHP[bx, by, bz];

                                                    blockListContent[x, y, z, a * 6] = (byte)blockList[x + a - 1, y, z];
                                                    blockListContent[x, y, z, a * 6 + 1] = x + a - 1;
                                                    blockListContent[x, y, z, a * 6 + 2] = y;
                                                    blockListContent[x, y, z, a * 6 + 3] = z;
                                                    //if (player != null)
                                                    //SendServerMessageToPlayer("green +y +x", player.NetConn);
                                                }
                                                else if (blockListContent[x, y, z, 1] == 0 && blockListContent[x, y, z, 2] == 3)// +y -> +z
                                                {
                                                    relx = 0;
                                                    rely = by - y;
                                                    relz = 0;

                                                    if (blockList[x, y, bz + rely] == BlockType.Water || blockList[x, y, bz + rely] == BlockType.Lava)
                                                    {//water in our way
                                                        if (blockList[x, y + 1, bz + rely] == BlockType.None)
                                                        {//push water up one
                                                            SetBlock((ushort)(x), (ushort)(y + 1), (ushort)(bz + rely), blockList[x, y, bz + rely], PlayerTeam.None);
                                                            blockListContent[x, y + 1, bz + rely, 1] = blockListContent[x, y, bz + rely, 1];//copy temperature
                                                            blockListContent[x, y + 1, bz + rely, 2] = blockListContent[x, y, bz + rely, 2];//copy blocks future type
                                                        }
                                                    }
                                                    SetBlock((ushort)(x), (ushort)(y), (ushort)(bz + rely), (BlockType)(blockListContent[x, y, z, a * 6]), blockCreatorTeam[bx, by, bz]);
                                                    blockListHP[x, y, bz + rely] = blockListHP[bx, by, bz];

                                                    blockListContent[x, y, z, a * 6] = (byte)blockList[x, y, z + a - 1];
                                                    blockListContent[x, y, z, a * 6 + 1] = x;
                                                    blockListContent[x, y, z, a * 6 + 2] = y;
                                                    blockListContent[x, y, z, a * 6 + 3] = z + a - 1;
                                                    //if (player != null)
                                                    //SendServerMessageToPlayer("green +y +z", player.NetConn);
                                                }
                                                else if (blockListContent[x, y, z, 1] == 0 && blockListContent[x, y, z, 2] == 2)// +y -> -x
                                                {
                                                    relx = 0;
                                                    rely = by - y;
                                                    relz = 0;

                                                    if (blockList[bx - rely, y, z] == BlockType.Water || blockList[bx - rely, y, z] == BlockType.Lava)
                                                    {//water in our way
                                                        if (blockList[bx - rely, y + 1, z] == BlockType.None)
                                                        {//push water up one
                                                            SetBlock((ushort)(bx - rely), (ushort)(y + 1), (ushort)(z), blockList[bx - rely, y, z], PlayerTeam.None);
                                                            blockListContent[bx - rely, y + 1, z, 1] = blockListContent[bx - rely, y, z, 1];//copy temperature
                                                            blockListContent[bx - rely, y + 1, z, 2] = blockListContent[bx - rely, y, z, 2];//copy blocks future type
                                                        }
                                                    }

                                                    SetBlock((ushort)(bx - rely), (ushort)(y), (ushort)(z), (BlockType)(blockListContent[x, y, z, a * 6]), blockCreatorTeam[bx, by, bz]);
                                                    blockListHP[bx - rely, y, z] = blockListHP[bx, by, bz];

                                                    blockListContent[x, y, z, a * 6] = (byte)blockList[x - (a - 1), y, z];
                                                    blockListContent[x, y, z, a * 6 + 1] = x - (a - 1);
                                                    blockListContent[x, y, z, a * 6 + 2] = y;
                                                    blockListContent[x, y, z, a * 6 + 3] = z;
                                                    //if (player != null)
                                                    //SendServerMessageToPlayer("green +y -x", player.NetConn);
                                                }
                                                else if (blockListContent[x, y, z, 1] == 0 && blockListContent[x, y, z, 2] == 4)// +y -> -z
                                                {
                                                    relx = 0;
                                                    rely = by - y;
                                                    relz = 0;

                                                    if (blockList[x, y, bz - rely] == BlockType.Water || blockList[x, y, bz - rely] == BlockType.Lava)
                                                    {//water in our way
                                                        if (blockList[x, y + 1, bz - rely] == BlockType.None)
                                                        {//push water up one
                                                            SetBlock((ushort)(x), (ushort)(y + 1), (ushort)(bz - rely), blockList[x, y, bz - rely], PlayerTeam.None);
                                                            blockListContent[x, y + 1, bz - rely, 1] = blockListContent[x, y, bz - rely, 1];//copy temperature
                                                            blockListContent[x, y + 1, bz - rely, 2] = blockListContent[x, y, bz - rely, 2];//copy blocks future type
                                                        }
                                                    }

                                                    SetBlock((ushort)(x), (ushort)(y), (ushort)(bz - rely), (BlockType)(blockListContent[x, y, z, a * 6]), blockCreatorTeam[bx, by, bz]);
                                                    blockListHP[x, y, bz - rely] = blockListHP[bx, by, bz];

                                                    blockListContent[x, y, z, a * 6] = (byte)blockList[x, y, z - (a - 1)];
                                                    blockListContent[x, y, z, a * 6 + 1] = x;
                                                    blockListContent[x, y, z, a * 6 + 2] = y;
                                                    blockListContent[x, y, z, a * 6 + 3] = z - (a - 1);
                                                    //if (player != null)
                                                    //SendServerMessageToPlayer("green +y -z", player.NetConn);
                                                }
                                                //setblockdebris for visible changes
                                                SetBlock((ushort)(bx), (ushort)(by), (ushort)(bz), BlockType.None, PlayerTeam.None);
                                            }
                                    }
                                    else
                                    {
                                        blockListContent[x, y, z, a * 6] = 0;//clear block out
                                        blockListContent[x, y, z, a * 6 + 1] = 0;
                                        blockListContent[x, y, z, a * 6 + 2] = 0;
                                        blockListContent[x, y, z, a * 6 + 3] = 0;
                                        repairme = true;
                                        //if (player != null)
                                        //SendServerMessageToPlayer("Empty requires repair on " + a, player.NetConn); 
                                    }
                                }

                                if (blockListContent[x, y, z, 1] == 1)//swap between +x -> +y to +x
                                    blockListContent[x, y, z, 1] = 0;//blockListContent[x, y, z, 2];
                                else
                                    blockListContent[x, y, z, 1] = 1;//revert to its original position
                            }
                            else
                            {
                                if (player != null)
                                    SendServerMessageToPlayer("It's jammed!", player.NetConn);
                            }
                    }

                }
                else if (btn == 2)
                {
                    if (blockListContent[x, y, z, 1] != 1 && blockList[x, y + 1, z] != BlockType.None)//checks hinge vert / if it has a block
                    {
                        //SendServerMessageToPlayer("The hinge must returned to horizontal position.", player.NetConn);
                        blockListContent[x, y, z, 2] += 1;
                        if (blockListContent[x, y, z, 2] > 4)
                            blockListContent[x, y, z, 2] = 0;

                        if (blockListContent[x, y, z, 2] == 1)//1 is not a viable direction to set
                            blockListContent[x, y, z, 2] = 2;

                        if (player != null)
                        {
                            string direction = "";
                            if (blockListContent[x, y, z, 2] == 0) //+x
                                direction = "North";
                            else if (blockListContent[x, y, z, 2] == 2) //-x
                                direction = "South";
                            else if (blockListContent[x, y, z, 2] == 3) //+z
                                direction = "East";
                            else if (blockListContent[x, y, z, 2] == 4) //-z
                                direction = "West";

                            SendServerMessageToPlayer("The hinge was rotated to face " + direction + ".", player.NetConn);
                        }

                        for (int a = 2; a < 7; a++)
                        {
                            if (blockListContent[x, y, z, a * 6] > 0)
                            {
                                if (blockListContent[x, y, z, 2] == 0) //+x
                                    DebrisEffectAtPoint(x + a, y, z, BlockType.Highlight, 1);
                                else if (blockListContent[x, y, z, 2] == 2) //-x
                                    DebrisEffectAtPoint(x - a, y, z, BlockType.Highlight, 1);
                                else if (blockListContent[x, y, z, 2] == 3) //+z
                                    DebrisEffectAtPoint(x, y, z + a, BlockType.Highlight, 1);
                                else if (blockListContent[x, y, z, 2] == 4) //-z
                                    DebrisEffectAtPoint(x, y, z - a, BlockType.Highlight, 1);
                            }
                        }
                        //rotate without changing anything
                        return true;
                    }

                    blockListContent[x, y, z, 2] += 1;
                    if (blockListContent[x, y, z, 2] > 4)
                        blockListContent[x, y, z, 2] = 0;

                    if (blockListContent[x, y, z, 2] == 1)//1 is not a viable direction to set
                        blockListContent[x, y, z, 2] = 2;

                    //blockListContent[x, y, z, 2] = 3;//2 ;//-x -> +
                    blockListContent[x, y, z, 1] = 1;

                    if (player != null)
                    {
                        string direction = "";
                        if (blockListContent[x, y, z, 2] == 0) //+x
                            direction = "North";
                        else if (blockListContent[x, y, z, 2] == 2) //-x
                            direction = "South";
                        else if (blockListContent[x, y, z, 2] == 3) //+z
                            direction = "East";
                        else if (blockListContent[x, y, z, 2] == 4) //-z
                            direction = "West";

                        SendServerMessageToPlayer("The hinge was rotated to face " + direction + ".", player.NetConn);
                    }

                    PlayerTeam team = PlayerTeam.None;
                    if (player != null)
                        team = player.Team;

                    for (int a = 2; a < 7; a++)//7
                    {
                        if (blockListContent[x, y, z, 2] == 0)
                        {
                            if (!HingeBlockTypes(blockList[x + a - 1, y, z], team))
                                break;

                            blockListContent[x, y, z, a * 6] = (byte)blockList[x + a - 1, y, z];
                            blockListContent[x, y, z, a * 6 + 1] = x + a - 1;
                            blockListContent[x, y, z, a * 6 + 2] = y;
                            blockListContent[x, y, z, a * 6 + 3] = z;

                            DebrisEffectAtPoint((float)(blockListContent[x, y, z, a * 6 + 1]), (float)(blockListContent[x, y, z, a * 6 + 2]), (float)(blockListContent[x, y, z, a * 6 + 3]), BlockType.Highlight, 1);
                            //if (player != null)
                            //    SendServerMessageToPlayer("lposx:" + (a - 1) + " " + blockList[blockListContent[x, y, z, a * 6 + 1], blockListContent[x, y, z, a * 6 + 2], blockListContent[x, y, z, a * 6 + 3]], player.NetConn);

                        }
                        else if (blockListContent[x, y, z, 2] == 2)
                        {
                            if (!HingeBlockTypes(blockList[x - (a - 1), y, z], team))
                                break;

                            blockListContent[x, y, z, a * 6] = (byte)blockList[x - (a - 1), y, z];
                            blockListContent[x, y, z, a * 6 + 1] = x - (a - 1);
                            blockListContent[x, y, z, a * 6 + 2] = y;
                            blockListContent[x, y, z, a * 6 + 3] = z;

                            DebrisEffectAtPoint((float)(blockListContent[x, y, z, a * 6 + 1]), (float)(blockListContent[x, y, z, a * 6 + 2]), (float)(blockListContent[x, y, z, a * 6 + 3]), BlockType.Highlight, 1);
                            //if (player != null)
                            //    SendServerMessageToPlayer("lnegx:" + (a - 1) + " " + blockList[blockListContent[x, y, z, a * 6 + 1], blockListContent[x, y, z, a * 6 + 2], blockListContent[x, y, z, a * 6 + 3]], player.NetConn);

                        }
                        else if (blockListContent[x, y, z, 2] == 3)
                        {
                            if (!HingeBlockTypes(blockList[x, y, z + a - 1], team))
                                break;

                            blockListContent[x, y, z, a * 6] = (byte)blockList[x, y, z + a - 1];
                            blockListContent[x, y, z, a * 6 + 1] = x;
                            blockListContent[x, y, z, a * 6 + 2] = y;
                            blockListContent[x, y, z, a * 6 + 3] = z + a - 1;

                            DebrisEffectAtPoint((float)(blockListContent[x, y, z, a * 6 + 1]), (float)(blockListContent[x, y, z, a * 6 + 2]), (float)(blockListContent[x, y, z, a * 6 + 3]), BlockType.Highlight, 1);
                            //if (player != null)
                            //    SendServerMessageToPlayer("lposz:" + (a - 1) + " " + blockList[blockListContent[x, y, z, a * 6 + 1], blockListContent[x, y, z, a * 6 + 2], blockListContent[x, y, z, a * 6 + 3]], player.NetConn);

                        }
                        else if (blockListContent[x, y, z, 2] == 4)
                        {
                            if (!HingeBlockTypes(blockList[x, y, z - (a - 1)], team))
                                break;

                            blockListContent[x, y, z, a * 6] = (byte)blockList[x, y, z - (a - 1)];
                            blockListContent[x, y, z, a * 6 + 1] = x;
                            blockListContent[x, y, z, a * 6 + 2] = y;
                            blockListContent[x, y, z, a * 6 + 3] = z - (a - 1);

                            DebrisEffectAtPoint((float)(blockListContent[x, y, z, a * 6 + 1]), (float)(blockListContent[x, y, z, a * 6 + 2]), (float)(blockListContent[x, y, z, a * 6 + 3]), BlockType.Highlight, 1);

                            //if (player != null)
                            //    SendServerMessageToPlayer("lnegz:" + (a - 1) + " " + blockList[blockListContent[x, y, z, a * 6 + 1], blockListContent[x, y, z, a * 6 + 2], blockListContent[x, y, z, a * 6 + 3]], player.NetConn);

                        }
                    }
                }
                return true;
            }

            if (blockList[x, y, z] != BlockType.None && blockList[x, y, z] != BlockType.Water && blockList[x, y, z] != BlockType.Lava && blockList[x, y, z] != BlockType.Lever && blockList[x, y, z] != BlockType.Plate && player == null)
            {
                //activated by a lever?
                Vector3 originVector = new Vector3(x, y, z);
                Vector3 destVector = new Vector3(ox, oy, oz);

                Vector3 finalVector = destVector - originVector;
                finalVector.Normalize();
                blockListContent[x, y, z, 10] = 1;
                blockListContent[x, y, z, 11] = (int)(finalVector.X * 100);
                blockListContent[x, y, z, 12] = (int)(finalVector.Y * 100) + 50;
                blockListContent[x, y, z, 13] = (int)(finalVector.Z * 100);
                blockListContent[x, y, z, 14] = x * 100;
                blockListContent[x, y, z, 15] = y * 100;
                blockListContent[x, y, z, 16] = z * 100;

                if (blockList[ox, oy, oz] == BlockType.Lever)
                {
                    for (int a = 1; a < 7; a++)
                    {
                        if (blockListContent[ox, oy, oz, a * 6] > 0)
                        {
                            if (blockListContent[ox, oy, oz, a * 6 + 1] == x && blockListContent[ox, oy, oz, a * 6 + 2] == y && blockListContent[ox, oy, oz, a * 6 + 3] == z)
                            {
                                return false;//this removes link from switch
                            }
                        }
                    }
                }
                else if (blockList[ox, oy, oz] == BlockType.Plate)
                {
                    for (int a = 1; a < 7; a++)
                    {
                        if (blockListContent[ox, oy, oz, a * 6] > 0)
                        {
                            if (blockListContent[ox, oy, oz, a * 6 + 1] == x && blockListContent[ox, oy, oz, a * 6 + 2] == y && blockListContent[ox, oy, oz, a * 6 + 3] == z)
                            {
                                return false;//this removes link from switch
                            }
                        }
                    }
                }
            }
            return false;
        }

        public void ResearchRecalculate(PlayerTeam team, int cc)
        {
            if (cc == 1)//modifying maximum hp
            {
                foreach (Player p in playerList.Values)
                    if (p.Team == team)
                    {
                        p.HealthMax += 20;// (uint)(ResearchComplete[(byte)team, cc] * 20);
                        SendResourceUpdate(p);
                    }
            }
            else if (cc == 2)
            {
                foreach (Player p in playerList.Values)
                    if (p.Team == team)
                    {
                        p.WeightMax += 1;// (uint)(ResearchComplete[(byte)team, cc]);
                        p.OreMax += 20;// (uint)(ResearchComplete[(byte)team, cc] * 20);
                        SendResourceUpdate(p);
                    }
            }
            else if (cc == 3)
            {
                teamRegeneration[(byte)team]++;
            }

           // SendResourceUpdate(p);
        }

        public void PlayerInteract(Player player, uint btn, uint x, uint y, uint z)
        {
            Trigger((int)(x), (int)(y), (int)(z), 0, 0, 0, (int)(btn), player);
            //we're not sending players origin or range checking currently
        }

        public void DepositOre(Player player)
        {
            uint depositAmount = Math.Min(50, player.Ore);
            player.Ore -= depositAmount;
            if (player.Team == PlayerTeam.Red)
                teamOreRed = Math.Min(teamOreRed + depositAmount, 9999);
            else
                teamOreBlue = Math.Min(teamOreBlue + depositAmount, 9999);
        }

        public void WithdrawOre(Player player)
        {
            if (player.Team == PlayerTeam.Red)
            {
                uint withdrawAmount = Math.Min(player.OreMax - player.Ore, Math.Min(50, teamOreRed));
                player.Ore += withdrawAmount;
                teamOreRed -= withdrawAmount;
            }
            else
            {
                uint withdrawAmount = Math.Min(player.OreMax - player.Ore, Math.Min(50, teamOreBlue));
                player.Ore += withdrawAmount;
                teamOreBlue -= withdrawAmount;
            }
        }

        public void GetNewHighestItem()
        {
            highestitem = 0;
            foreach (uint hi in itemIDList)
            {
                if (hi > highestitem)
                    highestitem = (int)hi;
            }
        }

        public void DeleteItem(uint ID)
        {
            SendSetItem(ID);
            itemList.Remove(ID);
            itemIDList.Remove(ID);
            if (ID == highestitem)
            {
                GetNewHighestItem();
            }
        }

        public void GetItem(Player player,uint ID)
        {
            if (player.Alive)
            {    
                foreach (KeyValuePair<uint, Item> bPair in itemList)//itemList[ID] for speed?
                {
                    if (bPair.Value.ID == ID && bPair.Value.Disposing == false)
                    {

                        if (Distf((player.Position - Vector3.UnitY * 0.5f), bPair.Value.Position) < 1.2)
                        {
                            if (bPair.Value.Type == ItemType.Ore)
                            {
                                while (player.Ore < player.OreMax)
                                {
                                    if (bPair.Value.Content[5] > 0)
                                    {
                                        player.Ore += 20;//add to players ore
                                        bPair.Value.Content[5] -= 1;//take away content of item

                                        if (player.Ore >= player.OreMax)
                                        {
                                            player.Ore = player.OreMax;//exceeded weight
                                            SendOreUpdate(player);
                                            SendCashUpdate(player);
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        SendOreUpdate(player);//run out of item content
                                        SendCashUpdate(player);
                                        
                                        break;
                                    }
                                }

                                if (bPair.Value.Content[5] > 0)//recalc scale if item still has content
                                {
                                    bPair.Value.Scale = 0.5f + (float)(bPair.Value.Content[5]) * 0.1f;
                                    SendItemScaleUpdate(bPair.Value);
                                }
                                else//removing item, no content left
                                {
                                    itemList[ID].Disposing = true;
                                    SendSetItem(ID);
                                    itemList.Remove(ID);
                                    itemIDList.Remove(ID);
                                    if (ID == highestitem)
                                    {
                                        GetNewHighestItem();
                                    }
                                }
                            }
                            else if (bPair.Value.Type == ItemType.Gold)
                            {
                                while (player.Weight < player.WeightMax)
                                {
                                    if (bPair.Value.Content[5] > 0)
                                    {
                                        player.Weight += 1;
                                        player.Cash += 10;
                                        bPair.Value.Content[5] -= 1;

                                        if(player.Weight >= player.WeightMax)
                                        {
                                            player.Weight = player.WeightMax;
                                            SendWeightUpdate(player);
                                            SendCashUpdate(player); 
                                            break;
                                        }
                                    }
                                    else//item out of content
                                    {
                                        SendWeightUpdate(player);
                                        SendCashUpdate(player); 
                                        break;
                                    }
                                }

                                if (bPair.Value.Content[5] > 0)//recalc scale if item remains
                                {
                                    bPair.Value.Scale = 0.5f + (float)(bPair.Value.Content[5]) * 0.1f;
                                    SendItemScaleUpdate(bPair.Value);
                                }
                                else//item out of content
                                {
                                    itemList[ID].Disposing = true;
                                    SendSetItem(ID);
                                    itemList.Remove(ID);
                                    itemIDList.Remove(ID);
                                    if (ID == highestitem)
                                    {
                                        GetNewHighestItem();
                                    }
                                }
                            }
                            else if(bPair.Value.Type == ItemType.Artifact)
                            {
                                if (player.Content[10] == 0 && itemList[ID].Content[6] == 0)//[6] = locked//empty artifact slot
                                {
                                    player.Content[10] = (int)(itemList[ID].Content[10]);//artifact type
                                    SendContentSpecificUpdate(player, 10);//tell player 
                                    SendPlayerContentUpdate(player, 10);//tell everyone else
                                    itemList[ID].Disposing = true;
                                    SendSetItem(ID);
                                    itemList.Remove(ID);
                                    itemIDList.Remove(ID);
                                    if (ID == highestitem)
                                    {
                                        GetNewHighestItem();
                                    }
                                }
                            }
                            else if (bPair.Value.Type == ItemType.Diamond)
                            {
                                if (player.Weight < player.WeightMax)
                                { 
                                    player.Weight += 1;   
                                    SendWeightUpdate(player);
                                    player.Content[11] += 1;//shardcount
                                    SendContentSpecificUpdate(player, 11);//tell player 
                                    SendPlayerContentUpdate(player, 11);//tell everyone else
                                    itemList[ID].Disposing = true;
                                    SendSetItem(ID);
                                    itemList.Remove(ID);
                                    itemIDList.Remove(ID);
                                    if (ID == highestitem)
                                    {
                                        GetNewHighestItem();
                                    }
                                    SendServerMessageToPlayer("You now possess a diamond to fuel our forge!", player.NetConn);
                                }
                            }
                            else
                            {
                                //just remove this unknown item
                                itemList[ID].Disposing = true;
                                SendSetItem(ID);
                                itemList.Remove(ID);
                                itemIDList.Remove(ID);
                                if (ID == highestitem)
                                {
                                    GetNewHighestItem();
                                }
                            }

                            PlaySound(InfiniminerSound.CashDeposit, player.Position);
                        }
                        return;
                    }
                }
            }
        }
        public void DepositCash(Player player)
        {
            if (player.Cash <= 0)
                return;

            player.Score += player.Cash;

            if (!varGetB("sandbox"))
            {
                if (player.Team == PlayerTeam.Red)
                    teamCashRed += player.Cash;
                else
                    teamCashBlue += player.Cash;
               // SendServerMessage("SERVER: " + player.Handle + " HAS EARNED $" + player.Cash + " FOR THE " + GetTeamName(player.Team) + " TEAM!");
            }

            PlaySound(InfiniminerSound.CashDeposit, player.Position);
            ConsoleWrite("DEPOSIT_CASH: " + player.Handle + ", " + player.Cash);
            
            player.Cash = 0;
            player.Weight = (uint)(player.Content[11]);//weight is now only diamonds on hand

            foreach (Player p in playerList.Values)
                SendResourceUpdate(p);
        }

        public string GetTeamName(PlayerTeam team)
        {
            switch (team)
            {
                case PlayerTeam.Red:
                    return "RED";
                case PlayerTeam.Blue:
                    return "BLUE";
            }
            return "";
        }

        public void SendServerMessageToPlayer(string message, NetConnection conn)
        {
            if (conn.Status == NetConnectionStatus.Connected)
            {
                NetBuffer msgBuffer = netServer.CreateBuffer();
                msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);
                msgBuffer.Write((byte)ChatMessageType.SayAll);
                msgBuffer.Write(Defines.Sanitize(message));

                netServer.SendMessage(msgBuffer, conn, NetChannel.ReliableInOrder3);
            }
        }

        public void SendServerMessage(string message)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);
            msgBuffer.Write((byte)ChatMessageType.SayAll);
            msgBuffer.Write(Defines.Sanitize(message));
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder3);
        }

        // Lets a player know about their resources.
        public void SendResourceUpdate(Player player)
        {
            if (player.NetConn.Status != NetConnectionStatus.Connected)
                return;

            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ResourceUpdate);
            msgBuffer.Write((uint)player.Ore);
            msgBuffer.Write((uint)player.Cash);
            msgBuffer.Write((uint)player.Weight);
            msgBuffer.Write((uint)player.OreMax);
            msgBuffer.Write((uint)player.WeightMax);
            msgBuffer.Write((uint)(player.Team == PlayerTeam.Red ? teamOreRed : teamOreBlue));
            msgBuffer.Write((uint)teamCashRed);
            msgBuffer.Write((uint)teamCashBlue);
            msgBuffer.Write((uint)teamArtifactsRed);
            msgBuffer.Write((uint)teamArtifactsBlue);
            msgBuffer.Write((uint)winningCashAmount);
            msgBuffer.Write((uint)player.Health);
            msgBuffer.Write((uint)player.HealthMax);
           // msgBuffer.Write((int)player.Content[5]);
            netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder1);
        }

        public void SendTeamCashUpdate(Player player)
        {
            if (player.NetConn.Status != NetConnectionStatus.Connected)
                return;

            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.TeamCashUpdate);
            msgBuffer.Write((uint)teamCashRed);
            msgBuffer.Write((uint)teamCashBlue);
            netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder1);
        }

        public void SendTeamOreUpdate(Player player)
        {
            if (player.NetConn.Status != NetConnectionStatus.Connected)
                return;

            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.TeamOreUpdate);
            if (player.Team == PlayerTeam.Red)
                msgBuffer.Write((uint)teamOreRed);
            else if (player.Team == PlayerTeam.Blue)
                msgBuffer.Write((uint)teamOreBlue);
            else
                return;
            netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder1);
        }
        public void SendContentUpdate(Player player)
        {
            if (player.NetConn.Status != NetConnectionStatus.Connected)
                return;

            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ContentUpdate);

            for(int a = 0;a < 50; a++)
            msgBuffer.Write((int)(player.Content[a]));

            netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder1);
        }

        public void SendHealthUpdate(Player player)
        {
            if (player.NetConn.Status != NetConnectionStatus.Connected)
                return;

            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.HealthUpdate);
            msgBuffer.Write(player.Health);

            netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder1);
        }

        public void SendWeightUpdate(Player player)
        {
            if (player.NetConn.Status != NetConnectionStatus.Connected)
                return;

            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.WeightUpdate);
            msgBuffer.Write(player.Weight);

            netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder1);
        }

        public void SendOreUpdate(Player player)
        {
            if (player.NetConn.Status != NetConnectionStatus.Connected)
                return;

            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.OreUpdate);
            msgBuffer.Write(player.Ore);

            netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder1);
        }

        public void SendCashUpdate(Player player)
        {
            if (player.NetConn.Status != NetConnectionStatus.Connected)
                return;

            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.CashUpdate);
            msgBuffer.Write(player.Cash);

            netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder1);
        }

        public void SendActiveArtifactUpdate(PlayerTeam team, int cc)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ActiveArtifactUpdate);
            msgBuffer.Write((byte)team);
            msgBuffer.Write(cc);
            msgBuffer.Write(artifactActive[(byte)team,cc]);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder1);
        }

        public void SendItemUpdate(Item i)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ItemUpdate);
            msgBuffer.Write(i.ID);
            msgBuffer.Write(i.Position);

            foreach (NetConnection netConn in playerList.Keys)
               if (netConn.Status == NetConnectionStatus.Connected)
                   netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder1);
        }

        public void SendScoreUpdate()
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ScoreUpdate);
            msgBuffer.Write(teamArtifactsRed);
            msgBuffer.Write(teamArtifactsBlue);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder1);
        }

        public void SendItemContentSpecificUpdate(Item i, int cc)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ItemContentSpecificUpdate);
            msgBuffer.Write(i.ID);
            msgBuffer.Write(cc);
            msgBuffer.Write(i.Content[cc]);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder1);
        }
        public void SendItemScaleUpdate(Item i)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ItemScaleUpdate);
            msgBuffer.Write(i.ID);
            msgBuffer.Write(i.Scale);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder1);
        }

        public void SendContentSpecificUpdate(Player player, int s)
        {
            if (player.NetConn.Status != NetConnectionStatus.Connected)
                return;

            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ContentSpecificUpdate);
            msgBuffer.Write((int)(s));
            msgBuffer.Write((int)(player.Content[s]));

            netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder1);
        }

        public void SendPlayerPosition(Player player)
        {
            if (player.NetConn.Status != NetConnectionStatus.Connected)
                return;

            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerPosition);
            msgBuffer.Write(player.Position);
            netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableUnordered);
        }
        List<MapSender> mapSendingProgress = new List<MapSender>();

        public void TerminateFinishedThreads()
        {
            List<MapSender> mapSendersToRemove = new List<MapSender>();
            foreach (MapSender ms in mapSendingProgress)
            {
                if (ms.finished)
                {
                    ms.stop();
                    mapSendersToRemove.Add(ms);
                }
            }
            foreach (MapSender ms in mapSendersToRemove)
            {
                mapSendingProgress.Remove(ms);
            }
        }

        public void SendCurrentMap(NetConnection client)
        {
            MapSender ms = new MapSender(client, this, netServer, MAPSIZE,playerList[client].compression);
            mapSendingProgress.Add(ms);
        }

        /*public void SendCurrentMapB(NetConnection client)
        {
            Debug.Assert(MAPSIZE == 64, "The BlockBulkTransfer message requires a map size of 64.");
            
            for (byte x = 0; x < MAPSIZE; x++)
                for (byte y=0; y<MAPSIZE; y+=16)
                {
                    NetBuffer msgBuffer = netServer.CreateBuffer();
                    msgBuffer.Write((byte)InfiniminerMessage.BlockBulkTransfer);
                    msgBuffer.Write(x);
                    msgBuffer.Write(y);
                    for (byte dy=0; dy<16; dy++)
                        for (byte z = 0; z < MAPSIZE; z++)
                            msgBuffer.Write((byte)(blockList[x, y+dy, z]));
                    if (client.Status == NetConnectionStatus.Connected)
                        netServer.SendMessage(msgBuffer, client, NetChannel.ReliableUnordered);
                }
        }*/
        public void Auth_Slap(Player p, uint playerId)
        {
            foreach (Player pt in playerList.Values)
            {
                if(pt.ID == playerId)
                {
                    if (p.Content[10] == 8)//medical 
                    {
                        if (pt.Team == p.Team && pt.Alive)
                        {
                            if (pt.Health < pt.HealthMax)
                            {
                                pt.Health += 10;
                                p.Score += 2;
                                if (pt.Health > pt.HealthMax)
                                    pt.Health = pt.HealthMax;

                                SendHealthUpdate(pt);
                                EffectAtPoint(pt.Position, 1);
                            }
                        }
                    }
                    else
                    {
                        if (pt.Team != p.Team && pt.Alive)
                            if (Distf(p.Position, pt.Position) < 4.0f)//slap in range
                            {
                                if (pt.Health > 10)
                                {
                                    pt.Health -= 10;
                                    NetBuffer msgBuffer = netServer.CreateBuffer();
                                    msgBuffer.Write((byte)InfiniminerMessage.PlayerSlap);
                                    msgBuffer.Write(playerId);//getting slapped
                                    msgBuffer.Write(p.ID);//attacker
                                    SendHealthUpdate(pt);

                                    foreach (NetConnection netConn in playerList.Keys)
                                        if (netConn.Status == NetConnectionStatus.Connected)
                                            netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);

                                    if (p.Content[10] == 2)//vampiric personal
                                    {
                                        p.Health += 5;
                                        if (p.Health > p.HealthMax)
                                            p.Health = p.HealthMax;

                                        SendHealthUpdate(p);
                                    }
                                    if (pt.Content[10] == 7)//reflection personal
                                    {
                                        if (p.Health > 5)
                                        {
                                            p.Health -= 5;
                                            SendHealthUpdate(p);
                                        }
                                        else
                                            Player_Dead(p, "SLAPPED THEMSELVES SILLY!");
                                    }
                                    if (artifactActive[(byte)p.Team, 2] != 0)//vampiric team
                                    {
                                        p.Health += (uint)artifactActive[(byte)p.Team, 2] * 2;
                                        if (p.Health > p.HealthMax)
                                            p.Health = p.HealthMax;

                                        SendHealthUpdate(p);
                                    }
                                }
                                else
                                {
                                    Player_Dead(pt, "WAS SLAPPED DOWN!");//slapped to death
                                }

                            }
                    }
                    break;
                }
            }
        }

        public void SendPlayerPing(uint playerId)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerPing);
            msgBuffer.Write(playerId);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
        }

        public void SendPlayerUpdate(Player player)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerUpdate);
            msgBuffer.Write((uint)player.ID);
            msgBuffer.Write(player.Position);
            msgBuffer.Write(player.Heading);
            msgBuffer.Write((byte)player.Tool);

            if (player.QueueAnimationBreak)
            {
                player.QueueAnimationBreak = false;
                msgBuffer.Write(false);
            }
            else
                msgBuffer.Write(player.UsingTool);

            msgBuffer.Write((ushort)player.Score / 100);
            msgBuffer.Write((ushort)player.Health / 100);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.UnreliableInOrder1);
        }

        public void SendSetBeacon(Vector3 position, string text, PlayerTeam team)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.SetBeacon);
            msgBuffer.Write(position);
            msgBuffer.Write(text);
            msgBuffer.Write((byte)team);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }

        public void SendPlayerContentUpdate(Player p, int cc)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerContentUpdate);
            msgBuffer.Write(p.ID);
            msgBuffer.Write(cc);
            msgBuffer.Write(p.Content[cc]);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    if (playerList[netConn] != p)
                        netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }

        public void SendSetItem(uint id, ItemType iType, Vector3 position, PlayerTeam team, Vector3 heading)//update player joined also
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.SetItem);
            msgBuffer.Write((byte)iType);
            msgBuffer.Write(id);
            msgBuffer.Write(position);
            msgBuffer.Write((byte)team);
            msgBuffer.Write(heading);
            msgBuffer.Write(itemList[id].Content[1]);
            msgBuffer.Write(itemList[id].Content[2]);
            msgBuffer.Write(itemList[id].Content[3]);
            msgBuffer.Write(itemList[id].Content[10]);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }
        public void SendSetItem(uint id)//empty item with no heading
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.SetItemRemove);
            msgBuffer.Write(id);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }
        public void SendPlayerJoined(Player player)
        {
            NetBuffer msgBuffer;

            // Let this player know about other players.
            foreach (Player p in playerList.Values)
            {
                msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.PlayerJoined);
                msgBuffer.Write((uint)p.ID);
                msgBuffer.Write(p.Handle);
                msgBuffer.Write(p == player);
                msgBuffer.Write(p.Alive);
                if (player.NetConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder2);

                msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.PlayerSetTeam);
                msgBuffer.Write((uint)p.ID);
                msgBuffer.Write((byte)p.Team);
                if (player.NetConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder2);

                msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.PlayerSetClass);
                msgBuffer.Write((uint)p.ID);
                msgBuffer.Write((byte)p.Class);
                if (player.NetConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder2);
            }

            // Let this player know about active (aqua/water) artifacts.
            if (artifactActive[(byte)PlayerTeam.Blue, 4] > 0)
                SendActiveArtifactUpdate(PlayerTeam.Blue, 4);
            if (artifactActive[(byte)PlayerTeam.Red, 4] > 0)
                SendActiveArtifactUpdate(PlayerTeam.Red, 4);

            // Let this player know about all placed beacons and items.
            foreach (KeyValuePair<uint, Item> bPair in itemList)
            {
                Vector3 position = bPair.Value.Position;
                Vector3 heading = bPair.Value.Heading;
                
                msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.SetItem);
                msgBuffer.Write((byte)(bPair.Value.Type));
                msgBuffer.Write(bPair.Key);
                msgBuffer.Write(position);
                msgBuffer.Write((byte)bPair.Value.Team);
                msgBuffer.Write(heading);
                msgBuffer.Write(itemList[bPair.Key].Content[1]);
                msgBuffer.Write(itemList[bPair.Key].Content[2]);
                msgBuffer.Write(itemList[bPair.Key].Content[3]);
                msgBuffer.Write(itemList[bPair.Key].Content[10]);

                if (player.NetConn.Status == NetConnectionStatus.Connected)
                {
                    netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder2);

                    if (itemList[bPair.Key].Content[6] > 0)
                        SendItemContentSpecificUpdate(bPair.Value, 6);
                }

            }

            foreach (KeyValuePair<Vector3, Beacon> bPair in beaconList)
            {
                Vector3 position = bPair.Key;
                position.Y += 1; //fixme
                msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.SetBeacon);
                msgBuffer.Write(position);
                msgBuffer.Write(bPair.Value.ID);
                msgBuffer.Write((byte)bPair.Value.Team);

                if (player.NetConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder2);
            }

            // Let other players know about this player.
            msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerJoined);
            msgBuffer.Write((uint)player.ID);
            msgBuffer.Write(player.Handle);
            msgBuffer.Write(false);
            msgBuffer.Write(player.Alive);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn != player.NetConn && netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);

            SendPlayerRespawn(player);
            // Send this out just incase someone is joining at the last minute.
            if (winningTeam != PlayerTeam.None)
                BroadcastGameOver();

            // Send out a chat message.
            msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);
            msgBuffer.Write((byte)ChatMessageType.SayAll);
            msgBuffer.Write(player.Handle + " HAS JOINED THE ADVENTURE!");
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder3);
        }

        public void BroadcastGameOver()
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.GameOver);
            msgBuffer.Write((byte)winningTeam);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);     
        }

        public void SendPlayerLeft(Player player, string reason)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerLeft);
            msgBuffer.Write((uint)player.ID);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn != player.NetConn && netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);

            // Send out a chat message.
            msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);
            msgBuffer.Write((byte)ChatMessageType.SayAll);
            msgBuffer.Write(player.Handle + " " + reason);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder3);
        }

        public void SendPlayerSetTeam(Player player)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerSetTeam);
            msgBuffer.Write((uint)player.ID);
            msgBuffer.Write((byte)player.Team);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }

        public void SendPlayerSetClass(Player player)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerSetClass);
            msgBuffer.Write((uint)player.ID);
            msgBuffer.Write((byte)player.Class);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)// && playerList[netConn].Team == player.Team)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }

        public void SendPlayerDead(Player player)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerDead);
            msgBuffer.Write((uint)player.ID);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }

        public void SendPlayerRespawn(Player player)
        {
            if (!player.Alive && player.Team != PlayerTeam.None && player.respawnTimer < DateTime.Now)
            {
                //create respawn script
                // Respawn a few blocks above a safe position above altitude 0.
                bool positionFound = false;

                // Try 20 times; use a potentially invalid position if we fail.
                for (int i = 0; i < 30; i++)
                {
                    // Pick a random starting point.

                    Vector3 startPos = new Vector3(randGen.Next(basePosition[player.Team].X - 2, basePosition[player.Team].X + 2), randGen.Next(basePosition[player.Team].Y - 1, basePosition[player.Team].Y + 1), randGen.Next(basePosition[player.Team].Z - 2, basePosition[player.Team].Z + 2));

                    // See if this is a safe place to drop.
                    //for (startPos.Y = 63; startPos.Y >= 54; startPos.Y--)
                    //{
                        BlockType blockType = BlockAtPoint(startPos);
                        if (blockType == BlockType.Vacuum && BlockAtPoint(startPos - Vector3.UnitY*1.0f) == BlockType.Vacuum)
                        {
                            // We have found a valid place to spawn, so spawn a few above it.
                            player.Position = startPos;// +Vector3.UnitY * 5;
                            positionFound = true;
                            break;
                        }
                   // }

                    // If we found a position, no need to try anymore!
                    if (positionFound)
                        break;
                }
                // If we failed to find a spawn point, drop randomly.
                if (!positionFound)
                {
                    player.Position = new Vector3(randGen.Next(2, 62), 66, randGen.Next(2, 62));
                    ConsoleWrite("player had no space to spawn");
                }

                // Drop the player on the middle of the block, not at the corner.
                player.Position += new Vector3(0.5f, 0, 0.5f);
                //
                player.rCount = 0;
                player.rUpdateCount = 0;
                player.rSpeedCount = 0;
                NetBuffer msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.PlayerRespawn);
                msgBuffer.Write(player.Position);
                netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder3);
            }
        }
        public void SendPlayerAlive(Player player)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerAlive);
            msgBuffer.Write((uint)player.ID);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }

        public void PlaySound(InfiniminerSound sound, Vector3 position)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlaySound);
            msgBuffer.Write((byte)sound);
            msgBuffer.Write(true);
            msgBuffer.Write(position);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
        }

        public void PlaySoundForEveryoneElse(InfiniminerSound sound, Vector3 position, Player p)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlaySound);
            msgBuffer.Write((byte)sound);
            msgBuffer.Write(true);
            msgBuffer.Write(position);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                {
                    if (playerList[netConn] != p)
                    {
                        netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
                    }
                }
        }

        Thread updater;
        bool updated = true;

        public void CommitUpdate()
        {
            try
            {
                if (updated)
                {
                    if (updater != null && !updater.IsAlive)
                    {
                        updater.Abort();
                        updater.Join();
                    }
                    updated = false;
                    updater = new Thread(new ThreadStart(this.RunUpdateThread));
                    updater.Start();
                }
            }
            catch { }
        }

        public void RunUpdateThread()
        {
            if (!updated)
            {
                Dictionary<string, string> postDict = new Dictionary<string, string>();
                postDict["name"] = varGetS("name");
                postDict["game"] = "INFINIMINER";
                postDict["player_count"] = "" + playerList.Keys.Count;
                postDict["player_capacity"] = "" + varGetI("maxplayers");
                postDict["extra"] = GetExtraInfo();

                lastServerListUpdate = DateTime.Now;

                try
                {
                    HttpRequest.Post("http://apps.keithholman.net/post", postDict);
                    ConsoleWrite("PUBLICLIST: UPDATING SERVER LISTING");
                }
                catch (Exception)
                {
                    ConsoleWrite("PUBLICLIST: ERROR CONTACTING SERVER");
                }

                updated = true;
            }
        }
    }
}