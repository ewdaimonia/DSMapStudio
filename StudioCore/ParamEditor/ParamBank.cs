﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Windows.Forms;
using SoulsFormats;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using FSParam;
using StudioCore.Editor;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Tab;
using HKX2;
using static SoulsFormats.HKX;
using Octokit;

namespace StudioCore.ParamEditor
{
    /// <summary>
    /// Utilities for dealing with global params for a game
    /// </summary>
    public class ParamBank
    {
        public static ParamBank PrimaryBank = new ParamBank();
        public static ParamBank VanillaBank = new ParamBank();
        public static Dictionary<string, ParamBank> AuxBanks = new Dictionary<string, ParamBank>();


        public static string ClipboardParam = null;
        public static List<Param.Row> ClipboardRows = new List<Param.Row>();

        private static Dictionary<string, PARAMDEF> _paramdefs = null;
        private static Dictionary<string, Dictionary<ulong, PARAMDEF>> _patchParamdefs = null;


        private Param EnemyParam = null;
        internal AssetLocator AssetLocator = null;

        private Dictionary<string, Param> _params = null;
        private Dictionary<string, HashSet<int>> _vanillaDiffCache = null; //If param != vanillaparam
        private Dictionary<string, HashSet<int>> _primaryDiffCache = null; //If param != primaryparam

        private bool _pendingUpgrade = false;

        public static bool IsDefsLoaded { get; private set; } = false;
        public static bool IsMetaLoaded { get; private set; } = false;
        public bool IsLoadingParams { get; private set; } = false;

        public IReadOnlyDictionary<string, Param> Params
        {
            get
            {
                if (IsLoadingParams)
                {
                    return null;
                }
                return _params;
            }
        }

        private ulong _paramVersion;
        public ulong ParamVersion
        {
            get => _paramVersion;
        }

        public IReadOnlyDictionary<string, HashSet<int>> VanillaDiffCache
        {
            get
            {
                if (IsLoadingParams)
                {
                    return null;
                }
                {
                if (VanillaBank == this)
                    return null;
                }
                return _vanillaDiffCache;
            }
        }
        public IReadOnlyDictionary<string, HashSet<int>> PrimaryDiffCache
        {
            get
            {
                if (IsLoadingParams)
                {
                    return null;
                }
                {
                if (PrimaryBank == this)
                    return null;
                }
                return _primaryDiffCache;
            }
        }

        private static List<(string, PARAMDEF)> LoadParamdefs(AssetLocator assetLocator)
        {
            _paramdefs = new Dictionary<string, PARAMDEF>();
            _patchParamdefs = new Dictionary<string, Dictionary<ulong, PARAMDEF>>();
            var dir = assetLocator.GetParamdefDir();
            var files = Directory.GetFiles(dir, "*.xml");
            List<(string, PARAMDEF)> defPairs = new List<(string, PARAMDEF)>();
            foreach (var f in files)
            {
                var pdef = PARAMDEF.XmlDeserialize(f);
                _paramdefs.Add(pdef.ParamType, pdef);
                defPairs.Add((f, pdef));
            }

            // Load patch paramdefs
            var patches = assetLocator.GetParamdefPatches();
            foreach (var patch in patches)
            {
                var pdir = assetLocator.GetParamdefPatchDir(patch);
                var pfiles = Directory.GetFiles(pdir, "*.xml");
                foreach (var f in pfiles)
                {
                    var pdef = PARAMDEF.XmlDeserialize(f);
                    defPairs.Add((f, pdef));
                    if (!_patchParamdefs.ContainsKey(pdef.ParamType))
                    {
                        _patchParamdefs[pdef.ParamType] = new Dictionary<ulong, PARAMDEF>();
                    }
                    _patchParamdefs[pdef.ParamType].Add(patch, pdef);
                }
            }

            return defPairs;
        }

        public static void LoadParamMeta(List<(string, PARAMDEF)> defPairs, AssetLocator assetLocator)
        {
            var mdir = assetLocator.GetParammetaDir();
            foreach ((string f, PARAMDEF pdef) in defPairs)
            {
                var fName = f.Substring(f.LastIndexOf('\\') + 1);
                ParamMetaData.XmlDeserialize($@"{mdir}\{fName}", pdef);
            }
        }

        public CompoundAction LoadParamDefaultNames(string param = null, bool onlyAffectEmptyNames = false)
        {
            string dir = AssetLocator.GetParamNamesDir();
            string[] files = param == null ? Directory.GetFiles(dir, "*.txt") : new[]
            {
                Path.Combine(dir, $"{param}.txt"),
            };
            var actions = new List<EditorAction>();
            foreach (string f in files)
            {
                string fName = Path.GetFileNameWithoutExtension(f);
                if (!_params.ContainsKey(fName))
                    continue;
                string names = File.ReadAllText(f);
                (MassEditResult r, CompoundAction a) = MassParamEditCSV.PerformSingleMassEdit(this, names, fName, "Name", ' ', true, onlyAffectEmptyNames);
                if (r.Type != MassEditResultType.SUCCESS)
                {
                    TaskManager.warningList.TryAdd($"ParamNameImportFail {fName}", $"Could not apply name files for {fName}");
                    continue;
                }
                actions.Add(a);
            }
            return new CompoundAction(actions);
        }

        public CompoundAction PocketRingMassEnemyUpdate()
        {
            List<EditorAction> actions = new List<EditorAction>();

            List<Param.Row> newNpcParams = new List<Param.Row>();

            foreach (Param.Row curr in this._params["SpEffectParam"].Rows)
            {
                if (curr.ID >= 26500000 && curr.ID < 200000000 && curr.ID % 2 == 0)//!curr.Name.Contains("Speshel")
                {
                    string name = curr.Name;
                    foreach(Param.Row npcCurr in this._params["NpcParam"].Rows)
                    {
                        if (npcCurr.Name == name)
                        {
                            for (int i = 0; i < 32; i++) {
                                Int32 val = (Int32)npcCurr.GetCellHandleOrThrow($"spEffectID{i}").Value;
                                if (val == -1) {
                                    npcCurr.GetCellHandleOrThrow($"spEffectID{i}").SetValue(curr.ID);
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            return new CompoundAction(actions);
        }

        public CompoundAction GenerateNavmeshWorkArounds()
        {
            List<EditorAction> actions = new List<EditorAction>();

            List<Param.Row> newNpcParams = new List<Param.Row>();

            foreach (Param.Row curr in this._params["NpcParam"].Rows)
            {
                if (curr.ID >= 20100000 && curr.ID < 100000000)
                //if (curr.ID >= 40800000 && curr.ID < 40900352)
                    {
                    Param.Row newParm = new Param.Row(curr);

                    //if ((UInt32)newParm.GetCellHandleOrThrow("hp").Value > (UInt32)1000)
                    //{
                        //newParm.GetCellHandleOrThrow("itemLotId_enemy").SetValue(890002000);
                    //} 
                    //else if ((UInt32)newParm.GetCellHandleOrThrow("hp").Value > (UInt32)500)
                    //{
                        //newParm.GetCellHandleOrThrow("itemLotId_enemy").SetValue(890001000);
                    //} 
                    //else
                    //{
                    newParm.GetCellHandleOrThrow("itemLotId_enemy").SetValue(890000000);
                    //}

                    //base scaling
                    //newParm.GetCellHandleOrThrow("spEffectID3").SetValue(7060);

                    newParm.Name = $"AUTOGENNED {curr.Name}";
                    newParm.ID += 800000000;

                    newNpcParams.Add(newParm);
                }
            }

            List<Param.Row> newThinkNpcParams = new List<Param.Row>();

            foreach (Param.Row curr in this._params["NpcThinkParam"].Rows)
            {
                if(curr.ID >= 20100000 && curr.ID < 100000000)
                //if (curr.ID >= 40800000 && curr.ID < 40900352)
                {
                    Param.Row newParm = new Param.Row(curr);

                    //For navmesh workarounds
                    //newParm.GetCellHandleOrThrow("disablePathMove").SetValue((Byte)1);
                    //newParm.GetCellHandleOrThrow("enableNaviFlg_Edge").SetValue((Byte)1);
                    //newParm.GetCellHandleOrThrow("enableNaviFlg_LargeSpace").SetValue((Byte)1);
                    //newParm.GetCellHandleOrThrow("isNoAvoidHugeEnemy").SetValue((Byte)1);
                    //newParm.GetCellHandleOrThrow("actTypeOnFailedPath").SetValue((Byte)0);
                    //newParm.GetCellHandleOrThrow("actTypeOnNonBtlFailedPath").SetValue((Byte)0);

                    //For always find
                    newParm.GetCellHandleOrThrow("BattleStartDist").SetValue((UInt16)60);
                    newParm.GetCellHandleOrThrow("thinkAttr_doAdmirer").SetValue((Byte)0);
                    newParm.GetCellHandleOrThrow("disableDark").SetValue((Byte)1);
                    newParm.GetCellHandleOrThrow("nose_dist").SetValue((UInt16)9999);
                    newParm.GetCellHandleOrThrow("MemoryTargetForgetTime").SetValue((Single)9999.00);
                    newParm.GetCellHandleOrThrow("eye_dist").SetValue((UInt16)200);
                    newParm.GetCellHandleOrThrow("eye_angX").SetValue((Byte)90);
                    newParm.GetCellHandleOrThrow("eye_angY").SetValue((Byte)180);
                    newParm.GetCellHandleOrThrow("searchEye_dist").SetValue((UInt16)0);
                    newParm.GetCellHandleOrThrow("searchEye_angY").SetValue((Byte)0);
                    newParm.GetCellHandleOrThrow("SightTargetForgetTime").SetValue((Single)9999);
                    newParm.GetCellHandleOrThrow("SoundTargetForgetTime").SetValue((Single)9999);
                    newParm.GetCellHandleOrThrow("maxBackhomeDist").SetValue((UInt16)9999);
                    newParm.GetCellHandleOrThrow("backhomeDist").SetValue((UInt16)9999);
                    newParm.GetCellHandleOrThrow("backhomeBattleDist").SetValue((UInt16)9999);
                    newParm.GetCellHandleOrThrow("BackHomeLife_OnHitEneWal").SetValue((Single)0);
                    newParm.GetCellHandleOrThrow("backToHomeStuckAct").SetValue((Byte)1);
                    newParm.GetCellHandleOrThrow("TeamAttackEffectivity").SetValue((Byte)0);

                    newParm.Name = $"AUTOGENNED {curr.Name}";
                    newParm.ID += 800000000;

                    newThinkNpcParams.Add(newParm);
                }
            }

            this._params["NpcParam"].Rows = this._params["NpcParam"].Rows.AsQueryable().Concat(newNpcParams).ToList().AsReadOnly();
            this._params["NpcThinkParam"].Rows = this._params["NpcThinkParam"].Rows.AsQueryable().Concat(newThinkNpcParams).ToList().AsReadOnly();

            return new CompoundAction(actions);
        }

        public CompoundAction GenerateRandomCharacters()
        {
            Param.Row wretchBase = this._params["CharaInitParam"].Rows.FirstOrDefault(r => r.ID == 3009);
            Param.Row ryaBase = this._params["FaceParam"].Rows.FirstOrDefault(r => r.ID == 23130);
            Param.Row patchesBase = this._params["FaceParam"].Rows.FirstOrDefault(r => r.ID == 23090);

            Param.Row minMaleFaceBase = new Param.Row(patchesBase);
            Param.Row minFemaleFaceBase = new Param.Row(ryaBase);

            List<Param.Row> maleFacePool = this._params["FaceParam"].Rows
                        .Where((Param.Row curr) =>
                        curr.ID >= 23000
                        && curr.ID < 30000
                        //this excludes Kenneth who has a more feminine face
                        && (((Byte)curr.GetCellHandleOrThrow("gender").Value < (Byte)128)
                        || ((Byte)curr.GetCellHandleOrThrow("gender").Value == (Byte)128
                            && (Byte)curr.GetCellHandleOrThrow("beard_partsId").Value != (Byte)0
                            && (Byte)curr.GetCellHandleOrThrow("face_beard").Value != (Byte)0
                        ))).ToList();
            List <Param.Row> femaleFacePool = this._params["FaceParam"].Rows
                        .Where((Param.Row curr) =>
                        curr.ID >= 23000
                        && curr.ID < 30000
                        && (Byte)curr.GetCellHandleOrThrow("beard_partsId").Value == (Byte)0
                        && (Byte)curr.GetCellHandleOrThrow("face_beard").Value == (Byte)0
                        //this excludes about three female npcs who has more masculine faces (vyke's finger maiden and arganthy + 1 more)
                        //, but I am too lazy to traverse the charinitparams to get the actual bodytype
                        && (Byte)curr.GetCellHandleOrThrow("gender").Value >= (Byte)128).ToList();

            List<string> forbiddenParams = new List<string>()
            {
                "ID","Name","pad","pad2","skin_color_R","skin_color_G","skin_color_B"
                ,"hair_color_R","hair_color_G","hair_color_B"
                ,"body_hairColor_R","body_hairColor_G","body_hairColor_B"
                ,"beard_color_R","beard_color_G","beard_color_B"
                ,"eyebrow_color_R","eyebrow_color_G","eyebrow_color_B"
                ,"eyelash_color_R","eyelash_color_G","eyelash_color_B"
                ,"face_aroundEyeColor_R","face_aroundEyeColor_G","face_aroundEyeColor_B"
                ,"face_cheekColor_R","face_cheekColor_G","face_cheekColor_B"
                ,"face_eyeLineColor_R","face_eyeLineColor_G","face_eyeLineColor_B"
                ,"face_eyeShadowDownColor_R","face_eyeShadowDownColor_G","face_eyeShadowDownColor_B"
                ,"face_eyeShadowUpColor_R","face_eyeShadowUpColor_G","face_eyeShadowUpColor_B"
                ,"face_lipColor_R","face_lipColor_G","face_lipColor_B"
                //Right
                ,"eyeR_irisColor_R","eyeR_irisColor_G","eyeR_irisColor_B"
                ,"eyeR_cataractColor_R","eyeR_cataractColor_G","eyeR_cataractColor_B"
                ,"eyeR_scleraColor_R","eyeR_scleraColor_G","eyeR_scleraColor_B"
                //Left
                ,"eyeL_irisColor_R","eyeL_irisColor_G","eyeL_irisColor_B"
                ,"eyeL_cataractColor_R","eyeL_cataractColor_G","eyeL_cataractColor_B"
                ,"eyeL_scleraColor_R","eyeL_scleraColor_G","eyeL_scleraColor_B",
                //Age
                "age",
                "gender",
                "face_partsId",
                "hair_partsId",
                "beard_partsId",
            };
            List<Param.Row> hairs = this._params["CharMakeMenuListItemParam"].Rows.Where(r => r.ID >= 700 && r.ID < 732).ToList();
            foreach (Param.Cell _cell in minMaleFaceBase.Cells)
            {
                if (!forbiddenParams.Contains(_cell.Def.InternalName))
                {
                    Param.Row _val = maleFacePool.Aggregate(patchesBase, (Param.Row agg, Param.Row curr) => 
                        (Byte)curr.GetCellHandleOrThrow(_cell.Def.InternalName).Value 
                        < (Byte)agg.GetCellHandleOrThrow(_cell.Def.InternalName).Value 
                        ? curr : agg);

                    _cell.SetValue((Byte)_val.GetCellHandleOrThrow(_cell.Def.InternalName).Value);
                }
            }
            foreach (Param.Cell _cell in minFemaleFaceBase.Cells)
            {
                if (!forbiddenParams.Contains(_cell.Def.InternalName))
                {
                    Param.Row _val = femaleFacePool.Aggregate(ryaBase, (Param.Row agg, Param.Row curr) =>
                        (Byte)curr.GetCellHandleOrThrow(_cell.Def.InternalName).Value
                        < (Byte)agg.GetCellHandleOrThrow(_cell.Def.InternalName).Value
                        ? curr : agg);

                    _cell.SetValue((Byte)_val.GetCellHandleOrThrow(_cell.Def.InternalName).Value);
                }
            }
            Param.Row maxMaleFaceBase = new Param.Row(patchesBase);
            foreach (Param.Cell _cell in maxMaleFaceBase.Cells)
            {
                if (!forbiddenParams.Contains(_cell.Def.InternalName))
                {
                    Param.Row _val = maleFacePool.Aggregate(patchesBase, (Param.Row agg, Param.Row curr) =>
                        (Byte)curr.GetCellHandleOrThrow(_cell.Def.InternalName).Value
                        > (Byte)agg.GetCellHandleOrThrow(_cell.Def.InternalName).Value
                        ? curr : agg);

                    _cell.SetValue((Byte)_val.GetCellHandleOrThrow(_cell.Def.InternalName).Value);
                }
            }
            Param.Row maxFemaleFaceBase = new Param.Row(ryaBase);
            foreach (Param.Cell _cell in maxFemaleFaceBase.Cells)
            {
                if (!forbiddenParams.Contains(_cell.Def.InternalName))
                {
                    Param.Row _val = femaleFacePool.Aggregate(ryaBase, (Param.Row agg, Param.Row curr) =>
                        (Byte)curr.GetCellHandleOrThrow(_cell.Def.InternalName).Value
                        > (Byte)agg.GetCellHandleOrThrow(_cell.Def.InternalName).Value
                        ? curr : agg);

                    _cell.SetValue((Byte)_val.GetCellHandleOrThrow(_cell.Def.InternalName).Value);
                }
            }
            List<Param.Row> newCharInits = new List<Param.Row>();
            List<Param.Row> newFaceGens = new List<Param.Row>();
            List<EditorAction> actions = new List<EditorAction>();
            for (int i = 0; i < 501; i++)
            {
                Byte bodyType = (Byte)RollDice(2, 1, 0);

                Param.Row newFace = GenerateFaceParam(bodyType == 1 ? patchesBase : ryaBase, bodyType == 1 ? minMaleFaceBase : minFemaleFaceBase, bodyType == 1 ? maxMaleFaceBase : maxFemaleFaceBase, bodyType == 1 ? maleFacePool : femaleFacePool, bodyType, hairs, i);
                Param.Row newChar = GenerateCharInit(wretchBase, bodyType, newFace.ID, i);

                newCharInits.Add(newChar);
                newFaceGens.Add(newFace);
            }

            this._params["CharaInitParam"].Rows = this._params["CharaInitParam"].Rows.AsQueryable().Concat(newCharInits).ToList().AsReadOnly();
            this._params["FaceParam"].Rows = this._params["FaceParam"].Rows.AsQueryable().Concat(newFaceGens).ToList().AsReadOnly();

            return new CompoundAction(actions);
        }

        private Param.Row GenerateFaceParam(Param.Row baseFace, Param.Row minFaceBase, Param.Row maxFaceBase, List<Param.Row> facePool, Byte bodyType, List<Param.Row> hairs, int i)
        {
            Param.Row newFace = new Param.Row(baseFace);
            List<string> forbiddenParams = new List<string>()
            {
                "ID","Name","pad","pad2","skin_color_R","skin_color_G","skin_color_B"
                ,"hair_color_R","hair_color_G","hair_color_B"
                ,"body_hairColor_R","body_hairColor_G","body_hairColor_B"
                ,"beard_color_R","beard_color_G","beard_color_B"
                ,"eyebrow_color_R","eyebrow_color_G","eyebrow_color_B"
                ,"eyelash_color_R","eyelash_color_G","eyelash_color_B"
                ,"face_aroundEyeColor_R","face_aroundEyeColor_G","face_aroundEyeColor_B"
                ,"face_cheekColor_R","face_cheekColor_G","face_cheekColor_B"
                ,"face_eyeLineColor_R","face_eyeLineColor_G","face_eyeLineColor_B"
                ,"face_eyeShadowDownColor_R","face_eyeShadowDownColor_G","face_eyeShadowDownColor_B"
                ,"face_eyeShadowUpColor_R","face_eyeShadowUpColor_G","face_eyeShadowUpColor_B"
                ,"face_lipColor_R","face_lipColor_G","face_lipColor_B"
                //Right
                ,"eyeR_irisColor_R","eyeR_irisColor_G","eyeR_irisColor_B"
                ,"eyeR_cataractColor_R","eyeR_cataractColor_G","eyeR_cataractColor_B"
                ,"eyeR_scleraColor_R","eyeR_scleraColor_G","eyeR_scleraColor_B"
                //Left
                ,"eyeL_irisColor_R","eyeL_irisColor_G","eyeL_irisColor_B"
                ,"eyeL_cataractColor_R","eyeL_cataractColor_G","eyeL_cataractColor_B"
                ,"eyeL_scleraColor_R","eyeL_scleraColor_G","eyeL_scleraColor_B",
                //Age
                "age",
                "gender",
                "face_partsId",
                "hair_partsId",
                "beard_partsId",
            };


            
            //Get skintone from either gendered npcss
            //Param.Row randomSkinColor = facePool[RollDice(facePool.Count, 1, 0)];
            Param.Row randomSkinColor = this._params["FaceParam"].Rows[RollDice(this._params["FaceParam"].Rows.Count, 1, 0)];
            newFace.GetCellHandleOrThrow("skin_color_R").SetValue(randomSkinColor.GetCellHandleOrThrow("skin_color_R").Value);
            newFace.GetCellHandleOrThrow("skin_color_G").SetValue(randomSkinColor.GetCellHandleOrThrow("skin_color_G").Value);
            newFace.GetCellHandleOrThrow("skin_color_B").SetValue(randomSkinColor.GetCellHandleOrThrow("skin_color_B").Value);
            Param.Row randomHairColor = facePool[RollDice(facePool.Count, 1, 0)];
            Byte hairR = (Byte)randomHairColor.GetCellHandleOrThrow("hair_color_R").Value;
            Byte hairG = (Byte)randomHairColor.GetCellHandleOrThrow("hair_color_G").Value;
            Byte hairB = (Byte)randomHairColor.GetCellHandleOrThrow("hair_color_B").Value;
            MultiValueAssign(hairR, newFace, new string[] { "hair_color_R", "body_hairColor_R", "beard_color_R", "eyebrow_color_R", "eyelash_color_R" });
            MultiValueAssign(hairG, newFace, new string[] { "hair_color_G", "body_hairColor_G", "beard_color_G", "eyebrow_color_G", "eyelash_color_G" });
            MultiValueAssign(hairB, newFace, new string[] { "hair_color_B", "body_hairColor_B", "beard_color_B", "eyebrow_color_B", "eyelash_color_B" });

            Param.Row randomFaceParts = facePool[RollDice(facePool.Count, 1, 0)];
            MultiValueAssign(randomFaceParts, newFace, new string[] { "face_partsId" });

            Param.Row randomAge = facePool[RollDice(facePool.Count, 1, 0)];
            MultiValueAssign(randomAge, newFace, new string[] { "age" });

            Param.Row randomGender = facePool[RollDice(facePool.Count, 1, 0)];
            MultiValueAssign(randomGender, newFace, new string[] { "gender" });

            Param.Row randomEyes = facePool[RollDice(facePool.Count, 1, 0)];
            MultiValueAssign(randomEyes, newFace, new string[] {                 
                "eyeR_irisColor_R","eyeR_irisColor_G","eyeR_irisColor_B"
                ,"eyeR_cataractColor_R","eyeR_cataractColor_G","eyeR_cataractColor_B"
                ,"eyeR_scleraColor_R","eyeR_scleraColor_G","eyeR_scleraColor_B"
                ,"eyeL_irisColor_R","eyeL_irisColor_G","eyeL_irisColor_B"
                ,"eyeL_cataractColor_R","eyeL_cataractColor_G","eyeL_cataractColor_B"
                ,"eyeL_scleraColor_R","eyeL_scleraColor_G","eyeL_scleraColor_B" });

            Param.Row randomCheeks = facePool[RollDice(facePool.Count, 1, 0)];
            MultiValueAssign(randomCheeks, newFace, new string[] {
                "face_cheekColor_R","face_cheekColor_G","face_cheekColor_B"
                ,"face_lipColor_R","face_lipColor_G","face_lipColor_B" });

            Byte hairValue = Convert.ToByte(hairs[RollDice(hairs.Count(), 1, 0)].GetCellHandleOrThrow("value").Value);
            MultiValueAssign(hairValue, newFace, new string[] {
                "hair_partsId" });

            if(bodyType == 1)
            {
                Param.Row randomBeard = facePool[RollDice(facePool.Count, 1, 0)];
                MultiValueAssign(randomBeard, newFace, new string[] {
                "beard_partsId" });
            }

            Param.Row randomShadow = facePool[RollDice(facePool.Count, 1, 0)];
            MultiValueAssign(randomShadow, newFace, new string[] {
                "face_aroundEyeColor_R","face_aroundEyeColor_G","face_aroundEyeColor_B"
                ,"face_eyeLineColor_R","face_eyeLineColor_G","face_eyeLineColor_B"
                ,"face_eyeShadowDownColor_R","face_eyeShadowDownColor_G","face_eyeShadowDownColor_B"
                ,"face_eyeShadowUpColor_R","face_eyeShadowUpColor_G","face_eyeShadowUpColor_B"
                });

            foreach (Param.Cell _cell in newFace.Cells)
            {
                if(!forbiddenParams.Contains(_cell.Def.InternalName))
                {
                    Param.Cell _minVal = minFaceBase.GetCellHandleOrThrow(_cell.Def.InternalName);
                    Param.Cell _maxVal = maxFaceBase.GetCellHandleOrThrow(_cell.Def.InternalName);

                    Byte _randVal = Convert.ToByte(RollDice(Convert.ToInt32(_maxVal.Value)+1, 1, Convert.ToInt32(_minVal.Value)));

                    _cell.SetValue(_randVal);
                }
            }

            newFace.ID = 70000 + i;
            newFace.Name = $"AUTOGENNED_ST Chars {i}";

            return newFace;
        }

        private void MultiValueAssign(Param.Row fromRow, Param.Row toRow, string[] args)
        {
            foreach(string arg in args)
            {
                toRow.GetCellHandleOrThrow(arg).SetValue(fromRow.GetCellHandleOrThrow(arg).Value);
            }
        }

        private void MultiValueAssign(Byte val, Param.Row toRow, string[] args)
        {
            foreach (string arg in args)
            {
                toRow.GetCellHandleOrThrow(arg).SetValue(val);
            }
        }

        private Param.Row GenerateCharInit(Param.Row wretchBase, Byte bodyType, int faceParamId, int i)
        {
            Param.Row newChar = new Param.Row(wretchBase);
            Dictionary<string, int> stats = new Dictionary<string, int>()
                {
                    { "baseVit", RollDice(7, 5) },
                    { "baseWil", RollDice(7, 5) },
                    { "baseEnd", RollDice(7, 5) },
                    { "baseStr", RollDice(7, 5) },
                    { "baseDex", RollDice(7, 5) },
                    { "baseMag", RollDice(7, 5) },
                    { "baseFai", RollDice(7, 5) },
                    { "baseLuc", RollDice(7, 5) },
                };
            //Pick one "strong" stat
            int strongStat = RollDice(7, 1, 0);

            switch (strongStat) {
                case (0):
                    stats["baseVit"] += 20;
                    break;
                case (1):
                    stats["baseWil"] += 20;
                    break;
                case (2):
                    stats["baseEnd"] += 20;
                    break;
                case (3):
                    stats["baseStr"] += 20;
                    break;
                case (4):
                    stats["baseDex"] += 20;
                    break;
                case (5):
                    stats["baseMag"] += 20;
                    break;
                case (6):
                    stats["baseFai"] += 20;
                    break;
                case (7):
                    stats["baseLuc"] += 20;
                    break;
            }

            //Stats
            foreach (KeyValuePair<string, int> stat in stats)
            {
                newChar.GetCellHandleOrThrow(stat.Key).SetValue((Byte)(10 + stat.Value));
            }

            //Set soulLv based on number of stats added
            int totalAddedStats = stats.Aggregate(0, (agg, curr) => curr.Value + agg);
            newChar.GetCellHandleOrThrow("soulLv").SetValue((Int16)(totalAddedStats + 1));

            //Cosmetics
            newChar.GetCellHandleOrThrow("npcPlayerSex").SetValue(bodyType);

            //Equipment
            double runningEquipLoad = GetEquipLoad(stats["baseEnd"]);

            List<int> mainWepTypes = new List<int>() { 
                1,3,5,7,9,11,13,14,15,16,17,19,21,23,24,25,28,29,31,35,37,39,41,
            };//Removed 14 since it was empty

            List<int> secondaryWepTypes = new List<int>() {
                1,3,5,7,9,11,13,14,15,16,17,19,21,23,24,25,28,29,31,35,37,39,41,
                50,51,53,55,65,67,69,87
            };

            int weaponTyp1 = mainWepTypes[RollDice(mainWepTypes.Count, 1, 0)];
            int weaponTyp2 = secondaryWepTypes[RollDice(secondaryWepTypes.Count, 1, 0)];

            List<Param.Row> weaponPool1 = this._params["EquipParamWeapon"].Rows.Where(w =>
                (Byte)w.GetCellHandleOrThrow("isCustom").Value == 1
                && (Int32)w.GetCellHandleOrThrow("sortId").Value != 9999999
                && (UInt16)w.GetCellHandleOrThrow("wepType").Value != 61
                && (UInt16)w.GetCellHandleOrThrow("wepType").Value != 57
                && (UInt16)w.GetCellHandleOrThrow("wepType").Value != 65
                && (UInt16)w.GetCellHandleOrThrow("wepType").Value != 67
                && (UInt16)w.GetCellHandleOrThrow("wepType").Value != 69
                && (UInt16)w.GetCellHandleOrThrow("wepType").Value != 51
                && (UInt16)w.GetCellHandleOrThrow("wepType").Value != 55
                //&& (UInt16)w.GetCellHandleOrThrow("wepType").Value == weaponTyp1
                //filter out infusion weapons
                && ((Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 0
                || (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 2200
                )
                && (Byte)w.GetCellHandleOrThrow("properStrength").Value <= (Byte)stats["baseStr"]
                && (Byte)w.GetCellHandleOrThrow("properAgility").Value <= (Byte)stats["baseDex"]
                && (Byte)w.GetCellHandleOrThrow("properMagic").Value <= (Byte)stats["baseMag"]
                && (Byte)w.GetCellHandleOrThrow("properFaith").Value <= (Byte)stats["baseFai"]
                && (Byte)w.GetCellHandleOrThrow("properLuck").Value <= (Byte)stats["baseLuc"]
                ).ToList();

            List<Param.Row> weaponPool2 = this._params["EquipParamWeapon"].Rows.Where(w =>
                (Byte)w.GetCellHandleOrThrow("isCustom").Value == 1
                && (Int32)w.GetCellHandleOrThrow("sortId").Value != 9999999
                && (UInt16)w.GetCellHandleOrThrow("wepType").Value != 61
                && (UInt16)w.GetCellHandleOrThrow("wepType").Value != 57
                //&& (UInt16)w.GetCellHandleOrThrow("wepType").Value == weaponTyp2
                //filter out infusion weapons
                && ((Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 0
                || (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 2200
                //large shields
                || ((UInt16)w.GetCellHandleOrThrow("wepType").Value == 69
                && ((Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 8200
                    || (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 8300
                ))
                //med shields
                || ((UInt16)w.GetCellHandleOrThrow("wepType").Value == 67
                && ((Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 8100
                    || (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 8300
                ))
                //small shields
                || ((UInt16)w.GetCellHandleOrThrow("wepType").Value == 65
                && ((Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 8000
                   || (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 8500
                ))
                //crossbows
                || ((UInt16)w.GetCellHandleOrThrow("wepType").Value == 55
                && ((Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 3100
                    || (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 3200
                    || (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 3300
                ))
                //light bows
                || ((UInt16)w.GetCellHandleOrThrow("wepType").Value == 67
                && ((Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 8100
                    || (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 2200
                    || (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 3300
                ))
                //med bows
                || ((UInt16)w.GetCellHandleOrThrow("wepType").Value == 65
                && ((Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 8000
                    || (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 2200
                    || (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 3300
                ))
                //great bows
                || ((UInt16)w.GetCellHandleOrThrow("wepType").Value == 45
                && ((Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 8000
                    || (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 2200
                    || (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 3300
                )))
                && (Byte)w.GetCellHandleOrThrow("properStrength").Value <= (Byte)stats["baseStr"]
                && (Byte)w.GetCellHandleOrThrow("properAgility").Value <= (Byte)stats["baseDex"]
                && (Byte)w.GetCellHandleOrThrow("properMagic").Value <= (Byte)stats["baseMag"]
                && (Byte)w.GetCellHandleOrThrow("properFaith").Value <= (Byte)stats["baseFai"]
                && (Byte)w.GetCellHandleOrThrow("properLuck").Value <= (Byte)stats["baseLuc"]
                ).ToList();

            Param.Row weapon1 = weaponPool1[RollDice(weaponPool1.Count, 1, 0)];
            Param.Row weapon2 = weaponPool2[RollDice(weaponPool2.Count, 1, 0)];

            if ((Int16)weapon1.GetCellHandleOrThrow("reinforceTypeId").Value == 0) {
                List<Param.Row> infusePool = this._params["EquipParamWeapon"].Rows.Where(w =>
                    (Int32)w.GetCellHandleOrThrow("originEquipWep").Value == weapon1.ID
                    ).ToList();

                weapon1 = infusePool[RollDice(infusePool.Count, 1, 0)];
            }

            if ((Int16)weapon2.GetCellHandleOrThrow("reinforceTypeId").Value == 0)
            {
                List<Param.Row> infusePool = this._params["EquipParamWeapon"].Rows.Where(w =>
                    (Int32)w.GetCellHandleOrThrow("originEquipWep").Value == weapon2.ID
                    ).ToList();

                weapon2 = infusePool[RollDice(infusePool.Count, 1, 0)];
            }

            runningEquipLoad -= (Single)weapon1.GetCellHandleOrThrow("weight").Value;
            runningEquipLoad -= (Single)weapon2.GetCellHandleOrThrow("weight").Value;

            newChar.GetCellHandleOrThrow("equip_Wep_Right").SetValue(weapon1.ID + 400000000);
            newChar.GetCellHandleOrThrow("wepParamType_Right1").SetValue((Byte)1);
            newChar.GetCellHandleOrThrow("equip_Wep_Left").SetValue(weapon2.ID + 400000000);
            newChar.GetCellHandleOrThrow("wepParamType_Left1").SetValue((Byte)1);

            //Bow
            //if ((UInt16)weapon2.GetCellHandleOrThrow("wepType").Value == 51 || (UInt16)weapon2.GetCellHandleOrThrow("wepType").Value == 51)
            //{
            newChar.GetCellHandleOrThrow("equip_Arrow").SetValue(50000000);
            newChar.GetCellHandleOrThrow("arrowNum").SetValue((UInt16)99);
            //}

            //Crossbow
            //if ((UInt16)weapon2.GetCellHandleOrThrow("wepType").Value == 55 || (UInt16)weapon2.GetCellHandleOrThrow("wepType").Value == 55)
            //{
                newChar.GetCellHandleOrThrow("equip_Bolt").SetValue(52000000);
                newChar.GetCellHandleOrThrow("boltNum").SetValue((UInt16)99);
            //}

            List<Param.Row> sealPool = this._params["EquipParamWeapon"].Rows.Where(w =>
                (Byte)w.GetCellHandleOrThrow("isCustom").Value == 1
                && (Int32)w.GetCellHandleOrThrow("sortId").Value != 9999999
                && (UInt16)w.GetCellHandleOrThrow("wepType").Value == 61
                && (UInt16)w.GetCellHandleOrThrow("wepType").Value != 57
                && (Byte)w.GetCellHandleOrThrow("properStrength").Value <= (Byte)stats["baseStr"]
                && (Byte)w.GetCellHandleOrThrow("properAgility").Value <= (Byte)stats["baseDex"]
                && (Byte)w.GetCellHandleOrThrow("properMagic").Value <= (Byte)stats["baseMag"]
                && (Byte)w.GetCellHandleOrThrow("properFaith").Value <= (Byte)stats["baseFai"]
                && (Byte)w.GetCellHandleOrThrow("properLuck").Value <= (Byte)stats["baseLuc"]
                ).ToList();

            if (sealPool.Count() > 0)
            {
                Param.Row seal = sealPool[RollDice(sealPool.Count, 1, 0)];
                newChar.GetCellHandleOrThrow("equip_Subwep_Left").SetValue(seal.ID + 400000000);
                newChar.GetCellHandleOrThrow("wepParamType_Left2").SetValue((Byte)1);

                //if player has seal, give incants
                List<Param.Row> incantPool = this._params["Magic"].Rows.Where(w =>
                    w.ID < 8000
                    && w.ID >= 6000
                    && (Byte)w.GetCellHandleOrThrow("requirementIntellect").Value <= (Byte)stats["baseMag"]
                    && (Byte)w.GetCellHandleOrThrow("requirementFaith").Value <= (Byte)stats["baseFai"]
                    && (Byte)w.GetCellHandleOrThrow("requirementLuck").Value <= (Byte)stats["baseLuc"]
                    ).ToList();

                if (incantPool.Count > 1)
                {
                    Param.Row inc1 = incantPool[RollDice(incantPool.Count, 1, 0)];
                    Param.Row inc2 = incantPool[RollDice(incantPool.Count, 1, 0)];

                    newChar.GetCellHandleOrThrow("equip_Spell_01").SetValue(inc1.ID);
                    newChar.GetCellHandleOrThrow("equip_Spell_03").SetValue(inc2.ID);
                }
            }

            List<Param.Row> staffPool = this._params["EquipParamWeapon"].Rows.Where(w =>
                (Byte)w.GetCellHandleOrThrow("isCustom").Value == 1
                && (Int32)w.GetCellHandleOrThrow("sortId").Value != 9999999
                && (UInt16)w.GetCellHandleOrThrow("wepType").Value != 61
                && (UInt16)w.GetCellHandleOrThrow("wepType").Value == 57
                && (Byte)w.GetCellHandleOrThrow("properStrength").Value <= (Byte)stats["baseStr"]
                && (Byte)w.GetCellHandleOrThrow("properAgility").Value <= (Byte)stats["baseDex"]
                && (Byte)w.GetCellHandleOrThrow("properMagic").Value <= (Byte)stats["baseMag"]
                && (Byte)w.GetCellHandleOrThrow("properFaith").Value <= (Byte)stats["baseFai"]
                && (Byte)w.GetCellHandleOrThrow("properLuck").Value <= (Byte)stats["baseLuc"]
                ).ToList();

            if (staffPool.Count() > 0)
            {
                Param.Row staff = staffPool[RollDice(staffPool.Count, 1, 0)];
                newChar.GetCellHandleOrThrow("equip_Subwep_Right").SetValue(staff.ID + 400000000);
                newChar.GetCellHandleOrThrow("wepParamType_Right2").SetValue((Byte)1);

                //if player has staff, give spells
                List<Param.Row> spellPool = this._params["Magic"].Rows.Where(w =>
                    w.ID >= 4000
                    && w.ID < 6000
                    && (Byte)w.GetCellHandleOrThrow("requirementIntellect").Value <= (Byte)stats["baseMag"]
                    && (Byte)w.GetCellHandleOrThrow("requirementFaith").Value <= (Byte)stats["baseFai"]
                    && (Byte)w.GetCellHandleOrThrow("requirementLuck").Value <= (Byte)stats["baseLuc"]
                    ).ToList();

                if (spellPool.Count > 1)
                {
                    Param.Row spl1 = spellPool[RollDice(spellPool.Count, 1, 0)];
                    Param.Row spl2 = spellPool[RollDice(spellPool.Count, 1, 0)];

                    newChar.GetCellHandleOrThrow("equip_Spell_02").SetValue(spl1.ID);
                    newChar.GetCellHandleOrThrow("equip_Spell_04").SetValue(spl2.ID);
                }
            }

            if (runningEquipLoad > 5)
            {
                List<Param.Row> pool = this._params["EquipParamProtector"].Rows.Where(w =>
                    (Byte)w.GetCellHandleOrThrow("bodyEquip").Value == 1
                    && (Single)w.GetCellHandleOrThrow("weight").Value <= runningEquipLoad
                    && (Int32)w.GetCellHandleOrThrow("sortId").Value != 99999
                    && (Int32)w.GetCellHandleOrThrow("sortId").Value != 999999
                    ).ToList();

                if (pool.Count() > 0)
                {
                    Param.Row body = pool[RollDice(pool.Count, 1, 0)];

                    if (body != null)
                    {
                        newChar.GetCellHandleOrThrow("equip_Armer").SetValue(body.ID);
                        runningEquipLoad -= (Single)body.GetCellHandleOrThrow("weight").Value;
                    }
                }
            }

            if (runningEquipLoad > 5)
            {
                List<Param.Row> pool = this._params["EquipParamProtector"].Rows.Where(w =>
(Byte)w.GetCellHandleOrThrow("legEquip").Value == 1
&& (Single)w.GetCellHandleOrThrow("weight").Value <= runningEquipLoad
&& (Int32)w.GetCellHandleOrThrow("sortId").Value != 99999
&& (Int32)w.GetCellHandleOrThrow("sortId").Value != 999999
).ToList();
                if(pool.Count() > 0)
                {
                    Param.Row greaves = pool[RollDice(pool.Count, 1, 0)];

                    if (greaves != null)
                    {
                        newChar.GetCellHandleOrThrow("equip_Leg").SetValue(greaves.ID);
                        runningEquipLoad -= (Single)greaves.GetCellHandleOrThrow("weight").Value;
                    }
                }
            }

            if (runningEquipLoad > 5)
            {
                List<Param.Row> pool = this._params["EquipParamProtector"].Rows.Where(w =>
(Byte)w.GetCellHandleOrThrow("armEquip").Value == 1
&& (Single)w.GetCellHandleOrThrow("weight").Value <= runningEquipLoad
&& (Int32)w.GetCellHandleOrThrow("sortId").Value != 99999
&& (Int32)w.GetCellHandleOrThrow("sortId").Value != 999999
).ToList();
                if (pool.Count() > 0)
                {
                    Param.Row gaunt = pool[RollDice(pool.Count, 1, 0)];

                    if (gaunt != null)
                    {
                        newChar.GetCellHandleOrThrow("equip_Gaunt").SetValue(gaunt.ID);
                        runningEquipLoad -= (Single)gaunt.GetCellHandleOrThrow("weight").Value;
                    }
                }
            }

            if (runningEquipLoad > 5)
            {
                List<Param.Row> pool = this._params["EquipParamProtector"].Rows.Where(w =>
(Byte)w.GetCellHandleOrThrow("headEquip").Value == 1
&& (Single)w.GetCellHandleOrThrow("weight").Value <= runningEquipLoad
&& (Int32)w.GetCellHandleOrThrow("sortId").Value != 99999
&& (Int32)w.GetCellHandleOrThrow("sortId").Value != 999999
).ToList();
                if (pool.Count() > 0)
                {
                    Param.Row helm = pool[RollDice(pool.Count, 1, 0)];

                    if (helm != null)
                    {
                        newChar.GetCellHandleOrThrow("equip_Helm").SetValue(helm.ID);
                        runningEquipLoad -= (Single)helm.GetCellHandleOrThrow("weight").Value;
                    }
                }
            }

            //talisman
            if (runningEquipLoad > 5)
            {
                List<Param.Row> pool = this._params["EquipParamAccessory"].Rows.Where(w =>
(Single)w.GetCellHandleOrThrow("weight").Value <= runningEquipLoad
&& (Int32)w.GetCellHandleOrThrow("sortId").Value != 99999
&& (Int32)w.GetCellHandleOrThrow("sortId").Value != 999999
&& (Int32)w.GetCellHandleOrThrow("sortId").Value != 9999999
).ToList();
                if (pool.Count() > 0)
                {
                    Param.Row tal = pool[RollDice(pool.Count, 1, 0)];

                    if (tal != null)
                    {
                        newChar.GetCellHandleOrThrow("equip_Accessory01").SetValue(tal.ID);
                    }
                }
            }

            //spirit ashes

           List<Param.Row> spiritAshpool = this._params["EquipParamGoods"].Rows.Where(w =>
(Byte)w.GetCellHandleOrThrow("opmeMenuType").Value == (Byte)13
&& (Byte)w.GetCellHandleOrThrow("goodsUseAnim").Value == (Byte)34
&& ((Int32)w.GetCellHandleOrThrow("reinforceMaterialId").Value == 10106
|| (Int32)w.GetCellHandleOrThrow("reinforceMaterialId").Value == 10006)
).ToList();
           if (spiritAshpool.Count() > 0)
           {
               Param.Row spirit = spiritAshpool[RollDice(spiritAshpool.Count, 1, 0)];
          
               if (spirit != null)
               {
                   newChar.GetCellHandleOrThrow("item_04").SetValue(spirit.ID);
                   newChar.GetCellHandleOrThrow("itemNum_04").SetValue((Byte)1);
                }
           }


            //Give estus
            newChar.GetCellHandleOrThrow("HpEstMax").SetValue((SByte)6);
            newChar.GetCellHandleOrThrow("MpEstMax").SetValue((SByte)6);
            newChar.GetCellHandleOrThrow("item_01").SetValue(1013);
            newChar.GetCellHandleOrThrow("itemNum_01").SetValue((Byte)99);
            newChar.GetCellHandleOrThrow("item_02").SetValue(1063);
            newChar.GetCellHandleOrThrow("itemNum_02").SetValue((Byte)99);
            newChar.GetCellHandleOrThrow("item_03").SetValue(130);
            newChar.GetCellHandleOrThrow("itemNum_03").SetValue((Byte)1);
            newChar.GetCellHandleOrThrow("item_05").SetValue(300);
            newChar.GetCellHandleOrThrow("itemNum_05").SetValue((Byte)99);
            newChar.GetCellHandleOrThrow("item_06").SetValue(2070);
            newChar.GetCellHandleOrThrow("itemNum_06").SetValue((Byte)1);
            newChar.GetCellHandleOrThrow("item_07").SetValue(2030);
            newChar.GetCellHandleOrThrow("itemNum_07").SetValue((Byte)50);
            newChar.GetCellHandleOrThrow("item_10").SetValue(9500);
            newChar.GetCellHandleOrThrow("itemNum_10").SetValue((Byte)99);
            

            newChar.ID = 70000 + i;
            newChar.Name = $"AUTOGENNED_ST Chars {i}";
            newChar.GetCellHandleOrThrow("npcPlayerFaceGenId").SetValue(faceParamId);

            return newChar;
        }

        public CompoundAction CreateRandomDropItemLots()
        {
            List<Param.Row> newItemLots = new List<Param.Row>();
            List<Param.Row> newCustWeps = new List<Param.Row>();
            List<Param.Row> newGems = new List<Param.Row>();

            Param.Row templateItemLotParam = new Param.Row(this._params["ItemLotParam_map"].Rows.FirstOrDefault(c=>c.ID == 997400));
            Param.Row templateCustParam = new Param.Row(this._params["EquipParamCustomWeapon"].Rows.FirstOrDefault(c=>c.ID == 10207));
            Param.Row templateGemParam = new Param.Row(this._params["EquipParamGem"].Rows.FirstOrDefault(c=>c.ID == 126));

            List<int> wepTypes = new List<int>() {
                1,3,5,7,9,11,13,14,15,16,17,19,21,23,24,25,28,29,31,35,37,39,41,
                50,51,53,55,65,67,69,87,57,61
            };

            List<Int32> existingSwordArtGemIds = this._params["EquipParamGem"].Rows.Select(w =>
                (Int32)w.GetCellHandleOrThrow("swordArtsParamId").Value
                ).ToList();

            List<Param.Row> swordArtsToBeCreated = this._params["SwordArtsParam"].Rows.Where(w =>
                !existingSwordArtGemIds.Contains(w.ID)
                ).ToList();

            List<Param.Row> smithingPool = this._params["EquipParamGoods"].Rows.Where(w =>
                (w.ID >= 10100 && w.ID < 10108)
                || w.ID == 10140
                || (w.ID >= 10160 && w.ID < 10169)
                || w.ID == 10200
                ).ToList();

            List<Param.Row> weaponPool = this._params["EquipParamWeapon"].Rows
                .Where(w =>
                (Int32)w.GetCellHandleOrThrow("swordArtsParamId").Value != 7
                && (Int32)w.GetCellHandleOrThrow("swordArtsParamId").Value != 1303
                && (Int32)w.GetCellHandleOrThrow("swordArtsParamId").Value != 1901
                && (Int32)w.GetCellHandleOrThrow("swordArtsParamId").Value != 2101
                && wepTypes.Contains((UInt16)w.GetCellHandleOrThrow("wepType").Value)
                //(Byte)w.GetCellHandleOrThrow("isCustom").Value == 1
                //&& (Int32)w.GetCellHandleOrThrow("sortId").Value != 9999999
                //&& ((Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 0 
                //|| (Int16)w.GetCellHandleOrThrow("reinforceTypeId").Value == 2200)
                )
            .OrderBy(o=> (byte)o.GetCellHandleOrThrow("rarity").Value).ToList();

            List<Param.Row> armorPool = this._params["EquipParamProtector"].Rows.Where(w =>
                (Int32)w.GetCellHandleOrThrow("sortId").Value != 99999
                && (Int32)w.GetCellHandleOrThrow("sortId").Value != 999999
                ).OrderBy(o => (byte)o.GetCellHandleOrThrow("rarity").Value).ToList();

            List<Param.Row> spellPool = this._params["EquipParamGoods"].Rows.Where(w =>
                w.ID >= 4000
                && w.ID < 6000
                ).OrderBy(o => (byte)o.GetCellHandleOrThrow("rarity").Value).ToList();

            List<Param.Row> incantPool = this._params["EquipParamGoods"].Rows.Where(w =>
                w.ID >= 6000
                && w.ID < 8000
                ).OrderBy(o => (byte)o.GetCellHandleOrThrow("rarity").Value).ToList();

            int i = 0;

            foreach (Param.Row curr in smithingPool)
            {
                Param.Row newLot = new Param.Row(templateItemLotParam);
                newLot.ID = 7600000 + i * 10;
                newLot.Name = $"AUTOGENNED {curr.Name}";
                newLot.GetCellHandleOrThrow("lotItemId01").SetValue(curr.ID);
                newItemLots.Add(newLot);
                i++;
            }

            i = 0;

            //Create custom gems
            foreach (Param.Row curr in swordArtsToBeCreated)
            {
                Param.Row newGem = new Param.Row(templateGemParam);
                newGem.ID = 400000000 + curr.ID;

                newGem.Name = $"Ash of War: {curr.Name}";
                newGem.GetCellHandleOrThrow("swordArtsParamId").SetValue(curr.ID);
                newGem.GetCellHandleOrThrow("canMountWep_SpearLarge").SetValue((Byte)1);
                newGem.GetCellHandleOrThrow("configurableWepAttr11").SetValue((Byte)1);
                newGem.GetCellHandleOrThrow("configurableWepAttr12").SetValue((Byte)1);
                newGems.Add(newGem);

                i++;
            }

            this._params["EquipParamGem"].Rows = this._params["EquipParamGem"].Rows.AsQueryable().Concat(newGems).ToList().AsReadOnly();

            i = 0;

            foreach (Param.Row curr in weaponPool)
            {
                //Create reinforced version
                //Need a custom gem for every swordArtsParam :(
                Param.Row newCust = new Param.Row(templateCustParam);
                newCust.ID = 400000000 + curr.ID;
                newCust.GetCellHandleOrThrow("reinforceLv").SetValue((Int32)curr.GetCellHandleOrThrow("materialSetId").Value == (Int32)2200 ? (Byte)5 : (Byte)14);
                newCust.GetCellHandleOrThrow("baseWepId").SetValue(curr.ID);

                Param.Row foundGem = this._params["EquipParamGem"].Rows.FirstOrDefault(g =>
                    (Int32)g.GetCellHandleOrThrow("swordArtsParamId").Value == (Int32)curr.GetCellHandleOrThrow("swordArtsParamId").Value
                );

                if (foundGem == null)
                {
                    System.Diagnostics.Trace.Write("Not found");
                }

                newCust.GetCellHandleOrThrow("gemId").SetValue(foundGem.ID);
                newCust.Name = $"AUTOGENNED Tier {(byte)curr.GetCellHandleOrThrow("rarity").Value} {curr.Name}";

                newCustWeps.Add(newCust);

                //
                Param.Row newLot = new Param.Row(templateItemLotParam);
                newLot.ID = 7610000 + i * 10;
                newLot.Name = $"AUTOGENNED Tier {(byte)curr.GetCellHandleOrThrow("rarity").Value} {curr.Name}";
                newLot.GetCellHandleOrThrow("lotItemId01").SetValue(newCust.ID);
                newLot.GetCellHandleOrThrow("lotItemCategory01").SetValue(6);
                newItemLots.Add(newLot);


                i++;
            }

            i = 0;

            foreach (Param.Row curr in armorPool)
            {
                Param.Row newLot = new Param.Row(templateItemLotParam);
                newLot.ID = 7640000 + i * 10;
                newLot.Name = $"AUTOGENNED Tier {(byte)curr.GetCellHandleOrThrow("rarity").Value} {curr.Name}";
                newLot.GetCellHandleOrThrow("lotItemId01").SetValue(curr.ID);
                newLot.GetCellHandleOrThrow("lotItemCategory01").SetValue(3);
                newItemLots.Add(newLot);
                i++;
            }

            i = 0;

            foreach (Param.Row curr in incantPool)
            {
                Param.Row newLot = new Param.Row(templateItemLotParam);
                newLot.ID = 7650000 + i * 10;
                newLot.Name = $"AUTOGENNED Tier {(byte)curr.GetCellHandleOrThrow("rarity").Value} {curr.Name}";
                newLot.GetCellHandleOrThrow("lotItemId01").SetValue(curr.ID);
                newItemLots.Add(newLot);
                i++;
            }

            i = 0;

            foreach (Param.Row curr in spellPool)
            {
                Param.Row newLot = new Param.Row(templateItemLotParam);
                newLot.ID = 7660000 + i * 10;
                newLot.Name = $"AUTOGENNED Tier {(byte)curr.GetCellHandleOrThrow("rarity").Value} {curr.Name}";
                newLot.GetCellHandleOrThrow("lotItemId01").SetValue(curr.ID);
                newItemLots.Add(newLot);
                i++;
            }

            this._params["EquipParamCustomWeapon"].Rows = this._params["EquipParamCustomWeapon"].Rows.AsQueryable().Concat(newCustWeps).ToList().AsReadOnly();
            this._params["ItemLotParam_map"].Rows = this._params["ItemLotParam_map"].Rows.AsQueryable().Concat(newItemLots).ToList().AsReadOnly();

            List<EditorAction> actions = new List<EditorAction>();
            return new CompoundAction(actions);
        }

        public CompoundAction CreateSTScaling()
        {
            List<Param.Row> newScalings = new List<Param.Row>();

            Param.Row templateScaling = new Param.Row(this._params["SpEffectParam"].Rows.FirstOrDefault(c => c.ID == 7680));

            templateScaling.GetCellHandleOrThrow("bGameClearBonus").SetValue((byte)0);

            for(int i = 0; i < 51; i++)
            {
                Param.Row newScaling = new Param.Row(templateScaling);
                newScaling.ID = 6307000 + i;
                newScaling.Name = $"AUTOGENNED ST SCALING - {i}";
                newScalings.Add(newScaling);
            }

            this._params["SpEffectParam"].Rows = this._params["SpEffectParam"].Rows.AsQueryable().Concat(newScalings).ToList().AsReadOnly();

            List<EditorAction> actions = new List<EditorAction>();
            return new CompoundAction(actions);
        }

        private int RollDice(int max = 6, int numDice = 1, int min = 1)
        {
            Random rnd = new Random();
            int total = 0;
            for(int i = 0; i < numDice; i++)
            {
                total += rnd.Next(min, max);
            }
            return total;
        }

        public ActionManager TrimNewlineChrsFromNames()
        {
            (MassEditResult r, ActionManager child) =
                MassParamEditRegex.PerformMassEdit(this, "param .*: id .*: name: replace \r:0", null);
            return child;
        }

        private void LoadParamFromBinder(IBinder parambnd, ref Dictionary<string, FSParam.Param> paramBank, out ulong version, bool checkVersion = false)
        {
            bool success = ulong.TryParse(parambnd.Version, out version);
            if (checkVersion && !success)
            {
                throw new Exception($@"Failed to get regulation version. Params might be corrupt.");
            }

            // Load every param in the regulation
            // _params = new Dictionary<string, PARAM>();
            foreach (var f in parambnd.Files)
            {
                if (!f.Name.ToUpper().EndsWith(".PARAM"))
                {
                    continue;
                }
                if (paramBank.ContainsKey(Path.GetFileNameWithoutExtension(f.Name)))
                {
                    continue;
                }
                if (f.Name.EndsWith("LoadBalancerParam.param") && AssetLocator.Type != GameType.EldenRing)
                {
                    continue;
                }
                FSParam.Param p = FSParam.Param.Read(f.Bytes);
                if (!_paramdefs.ContainsKey(p.ParamType) && !_patchParamdefs.ContainsKey(p.ParamType))
                {
                    continue;
                }

                // Try to fixup Elden Ring ChrModelParam for ER 1.06 because many have been saving botched params and
                // it's an easy fixup
                if (AssetLocator.Type == GameType.EldenRing &&
                    p.ParamType == "CHR_MODEL_PARAM_ST" &&
                    _paramVersion == 10601000)
                {
                    p.FixupERChrModelParam();
                }

                // Lookup the correct paramdef based on the version
                PARAMDEF def = null;
                if (_patchParamdefs.ContainsKey(p.ParamType))
                {
                    var keys = _patchParamdefs[p.ParamType].Keys.OrderByDescending(e => e);
                    foreach (var k in keys)
                    {
                        if (version >= k)
                        {
                            def = _patchParamdefs[p.ParamType][k];
                            break;
                        }
                    }
                }

                // If no patched paramdef was found for this regulation version, fallback to vanilla defs
                if (def == null)
                    def = _paramdefs[p.ParamType];

                try
                {
                    p.ApplyParamdef(def);
                    paramBank.Add(Path.GetFileNameWithoutExtension(f.Name), p);
                }
                catch(Exception e)
                {
                    var name = f.Name.Split("\\").Last();
                    TaskManager.warningList.TryAdd($"{name} DefFail",$"Could not apply ParamDef for {name}");
                }
            }
        }

        /// <summary>
        /// Checks for DeS paramBNDs and returns the name of the parambnd with the highest priority.
        /// </summary>
        private string GetDesGameparamName(string rootDirectory)
        {
            string name = "";
            name = "gameparamna.parambnd.dcx";
            if (File.Exists($@"{rootDirectory}\param\gameparam\{name}"))
            {
                return name;
            }
            name = "gameparamna.parambnd";
            if (File.Exists($@"{rootDirectory}\param\gameparam\{name}"))
            {
                return name;
            }
            name = "gameparam.parambnd.dcx";
            if (File.Exists($@"{rootDirectory}\param\gameparam\{name}"))
            {
                return name;
            }
            name = "gameparam.parambnd";
            if (File.Exists($@"{rootDirectory}\param\gameparam\{name}"))
            {
                return name;
            }
            return "";
        }

        private void LoadParamsDES()
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;

            string paramBinderName = GetDesGameparamName(mod);
            if (paramBinderName == "")
            {
                paramBinderName = GetDesGameparamName(dir);
            }

            // Load params
            var param = $@"{mod}\param\gameparam\{paramBinderName}";
            if (!File.Exists(param))
            {
                param = $@"{dir}\param\gameparam\{paramBinderName}";
            }

            if (!File.Exists(param))
            {
                throw new FileNotFoundException("Could not find DES parambnds. Functionality will be limited.");
            }
            LoadParamsDESFromFile(param);

            //DrawParam
            Dictionary<string, string> drawparams = new();
            if (Directory.Exists($@"{dir}\param\drawparam"))
            {
                foreach (var p in Directory.GetFiles($@"{dir}\param\drawparam", "*.parambnd.dcx"))
                {
                    drawparams[Path.GetFileNameWithoutExtension(p)] = p;
                }
            }
            if (Directory.Exists($@"{mod}\param\drawparam"))
            {
                foreach (var p in Directory.GetFiles($@"{mod}\param\drawparam", "*.parambnd.dcx"))
                {
                    drawparams[Path.GetFileNameWithoutExtension(p)] = p;
                }
            }
            foreach (var drawparam in drawparams)
            {
                LoadParamsDESFromFile(drawparam.Value);
            }
        }
        private void LoadVParamsDES()
        {
            string paramBinderName = GetDesGameparamName(AssetLocator.GameRootDirectory);

            LoadParamsDESFromFile($@"{AssetLocator.GameRootDirectory}\param\gameparam\{paramBinderName}");
            if (Directory.Exists($@"{AssetLocator.GameRootDirectory}\param\drawparam"))
            {
                foreach (var p in Directory.GetFiles($@"{AssetLocator.GameRootDirectory}\param\drawparam", "*.parambnd.dcx"))
                {
                    LoadParamsDS1FromFile(p);
                }
            }
        }
        private void LoadParamsDESFromFile(string path)
        {
            LoadParamFromBinder(BND3.Read(path), ref _params, out _paramVersion);
        }

        private void LoadParamsDS1()
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;
            if (!File.Exists($@"{dir}\\param\GameParam\GameParam.parambnd"))
            {
                //MessageBox.Show("Could not find DS1 regulation file. Functionality will be limited.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                //return null;
                throw new FileNotFoundException("Could not find DS1 parambnd. Functionality will be limited.");
            }
            // Load params
            var param = $@"{mod}\param\GameParam\GameParam.parambnd";
            if (!File.Exists(param))
            {
                param = $@"{dir}\param\GameParam\GameParam.parambnd";
            }
            LoadParamsDS1FromFile(param);

            //DrawParam
            Dictionary<string, string> drawparams = new();
            if (Directory.Exists($@"{dir}\param\DrawParam"))
            {
                foreach (var p in Directory.GetFiles($@"{dir}\param\DrawParam", "*.parambnd"))
                {
                    drawparams[Path.GetFileNameWithoutExtension(p)] = p;
                }
            }
            if (Directory.Exists($@"{mod}\param\DrawParam"))
            {
                foreach (var p in Directory.GetFiles($@"{mod}\param\DrawParam", "*.parambnd"))
                {
                    drawparams[Path.GetFileNameWithoutExtension(p)] = p;
                }
            }
            foreach (var drawparam in drawparams)
            {
                LoadParamsDS1FromFile(drawparam.Value);
            }
        }
        private void LoadVParamsDS1()
        {
            LoadParamsDS1FromFile($@"{AssetLocator.GameRootDirectory}\param\GameParam\GameParam.parambnd");
            if (Directory.Exists($@"{AssetLocator.GameRootDirectory}\param\DrawParam"))
            {
                foreach (var p in Directory.GetFiles($@"{AssetLocator.GameRootDirectory}\param\DrawParam", "*.parambnd"))
                {
                    LoadParamsDS1FromFile(p);
                }
            }
        }
        private void LoadParamsDS1FromFile(string path)
        {
            LoadParamFromBinder(BND3.Read(path), ref _params, out _paramVersion);
        }

        private void LoadParamsDS1R()
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;
            if (!File.Exists($@"{dir}\\param\GameParam\GameParam.parambnd.dcx"))
            {
                //MessageBox.Show("Could not find DS1 regulation file. Functionality will be limited.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                //return null;
                throw new FileNotFoundException("Could not find DS1 parambnd. Functionality will be limited.");
            }

            // Load params
            var param = $@"{mod}\param\GameParam\GameParam.parambnd.dcx";
            if (!File.Exists(param))
            {
                param = $@"{dir}\param\GameParam\GameParam.parambnd.dcx";
            }
            LoadParamsDS1RFromFile(param);

            //DrawParam
            Dictionary<string, string> drawparams = new();
            if (Directory.Exists($@"{dir}\param\DrawParam"))
            {
                foreach (var p in Directory.GetFiles($@"{dir}\param\DrawParam", "*.parambnd.dcx"))
                {
                    drawparams[Path.GetFileNameWithoutExtension(p)] = p;
                }
            }
            if (Directory.Exists($@"{mod}\param\DrawParam"))
            {
                foreach (var p in Directory.GetFiles($@"{mod}\param\DrawParam", "*.parambnd.dcx"))
                {
                    drawparams[Path.GetFileNameWithoutExtension(p)] = p;
                }
            }
            foreach (var drawparam in drawparams)
            {
                LoadParamsDS1RFromFile(drawparam.Value);
            }
        }
        private void LoadVParamsDS1R()
        {
            LoadParamsDS1RFromFile($@"{AssetLocator.GameRootDirectory}\param\GameParam\GameParam.parambnd.dcx");
            if (Directory.Exists($@"{AssetLocator.GameRootDirectory}\param\DrawParam"))
            {
                foreach (var p in Directory.GetFiles($@"{AssetLocator.GameRootDirectory}\param\DrawParam", "*.parambnd.dcx"))
                {
                    LoadParamsDS1FromFile(p);
                }
            }
        }
        private void LoadParamsDS1RFromFile(string path)
        {
            LoadParamFromBinder(BND3.Read(path), ref _params, out _paramVersion);
        }

        private void LoadParamsBBSekiro()
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;
            if (!File.Exists($@"{dir}\\param\gameparam\gameparam.parambnd.dcx"))
            {
                //MessageBox.Show("Could not find param file. Functionality will be limited.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                //return null;
                throw new FileNotFoundException("Could not find param file. Functionality will be limited.");
            }

            // Load params
            var param = $@"{mod}\param\gameparam\gameparam.parambnd.dcx";
            if (!File.Exists(param))
            {
                param = $@"{dir}\param\gameparam\gameparam.parambnd.dcx";
            }
            LoadParamsBBSekiroFromFile(param);
        }
        private void LoadVParamsBBSekiro()
        {
            LoadParamsBBSekiroFromFile($@"{AssetLocator.GameRootDirectory}\param\gameparam\gameparam.parambnd.dcx");
        }
        private void LoadParamsBBSekiroFromFile(string path)
        {
            LoadParamFromBinder(BND4.Read(path), ref _params, out _paramVersion);
        }

        /// <summary>
        /// Map related params.
        /// </summary>
        public readonly static List<string> DS2MapParamlist = new List<string>()
        {
            "demopointlight",
            "demospotlight",
            "eventlocation",
            "eventparam",
            "GeneralLocationEventParam",
            "generatorparam",
            "generatorregistparam",
            "generatorlocation",
            "generatordbglocation",
            "hitgroupparam",
            "intrudepointparam",
            "mapobjectinstanceparam",
            "maptargetdirparam",
            "npctalkparam",
            "treasureboxparam",
        };

        private static List<string> GetLooseParamsInDir(string dir)
        {
            List<string> looseParams = new();
            if (Directory.Exists($@"{dir}\Param"))
            {
                looseParams.AddRange(Directory.GetFileSystemEntries($@"{dir}\Param", @"*.param"));
            }
            return looseParams;
        }

        private void LoadParamsDS2(bool loose)
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;
            if (!File.Exists($@"{dir}\enc_regulation.bnd.dcx"))
            {
                //MessageBox.Show("Could not find DS2 regulation file. Functionality will be limited.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                //return null;
                throw new FileNotFoundException("Could not find DS2 regulation file. Functionality will be limited.");
            }
            if (!BND4.Is($@"{dir}\enc_regulation.bnd.dcx"))
            {
                MessageBox.Show("Attempting to decrypt DS2 regulation file, else functionality will be limited.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                //return;
            }

            // Load loose params (prioritizing ones in mod folder)
            List<string> looseParams = GetLooseParamsInDir(mod);
            if (Directory.Exists($@"{dir}\Param"))
            {
                // Include any params in game folder that are not in mod folder
                foreach (var path in Directory.GetFileSystemEntries($@"{dir}\Param", @"*.param"))
                {
                    if (looseParams.Find(e => Path.GetFileName(e) == Path.GetFileName(path)) == null)
                    {
                        // Project folder does not contain this loose param
                        looseParams.Add(path);
                    }
                }
            }

            // Load reg params
            var param = $@"{mod}\enc_regulation.bnd.dcx";
            if (!File.Exists(param))
            {
                param = $@"{dir}\enc_regulation.bnd.dcx";
            }
            string enemyFile = $@"{mod}\Param\EnemyParam.param";
            if (!File.Exists(enemyFile))
            {
                enemyFile = $@"{dir}\Param\EnemyParam.param";
            }
            LoadParamsDS2FromFile(looseParams, param, enemyFile, loose);
        }
        private void LoadVParamsDS2(bool loose)
        {
            if (!File.Exists($@"{AssetLocator.GameRootDirectory}\enc_regulation.bnd.dcx"))
            {
                throw new FileNotFoundException("Could not find Vanilla DS2 regulation file. Functionality will be limited.");
            }
            if (!BND4.Is($@"{AssetLocator.GameRootDirectory}\enc_regulation.bnd.dcx"))
            {
                MessageBox.Show("Attempting to decrypt DS2 regulation file, else functionality will be limited.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            // Load loose params
            var looseParams = GetLooseParamsInDir(AssetLocator.GameRootDirectory);

            LoadParamsDS2FromFile(looseParams, $@"{AssetLocator.GameRootDirectory}\enc_regulation.bnd.dcx", $@"{AssetLocator.GameRootDirectory}\Param\EnemyParam.param", loose);
        }
        private void LoadParamsDS2FromFile(List<string> looseParams, string path, string enemypath, bool loose)
        {
            BND4 paramBnd;
            if (!BND4.Is(path))
            {
                paramBnd = SFUtil.DecryptDS2Regulation(path);
            }
            // No need to decrypt
            else
            {
                paramBnd = BND4.Read(path);
            }
            var bndfile = paramBnd.Files.Find(x => Path.GetFileName(x.Name) == "EnemyParam.param");
            if (bndfile != null)
            {
                EnemyParam = Param.Read(bndfile.Bytes);
            }

            // Otherwise the param is a loose param
            if (File.Exists(enemypath))
            {
                EnemyParam = Param.Read(enemypath);
            }
            if (EnemyParam != null)
            {
                try
                {
                    PARAMDEF def = _paramdefs[EnemyParam.ParamType];
                    EnemyParam.ApplyParamdef(def);
                }
                catch (Exception e)
                {
                    TaskManager.warningList.TryAdd($"{EnemyParam.ParamType} DefFail", $"Could not apply ParamDef for {EnemyParam.ParamType}");
                }
            }
            LoadParamFromBinder(paramBnd, ref _params, out _paramVersion);

            foreach (var p in looseParams)
            {
                var name = Path.GetFileNameWithoutExtension(p);
                var lp = Param.Read(p);
                var fname = lp.ParamType;

                try
                {
                    if (loose)
                    {
                        // Loose params: override params already loaded via regulation
                        PARAMDEF def = _paramdefs[lp.ParamType];
                        lp.ApplyParamdef(def);
                        _params[name] = lp;
                    }
                    else
                    {
                        // Non-loose params: do not override params already loaded via regulation
                        if (!_params.ContainsKey(name))
                        {
                            PARAMDEF def = _paramdefs[lp.ParamType];
                            lp.ApplyParamdef(def);
                            _params.Add(name, lp);
                        }
                    }
                }
                catch (Exception e)
                {
                    TaskManager.warningList.TryAdd($"{fname} DefFail", $"Could not apply ParamDef for {fname}");
                }
            }
            
        }

        private void LoadParamsDS3(bool loose)
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;
            if (!File.Exists($@"{dir}\Data0.bdt"))
            {
                //MessageBox.Show("Could not find DS3 regulation file. Functionality will be limited.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                //return null;
                throw new FileNotFoundException("Could not find DS3 regulation file. Functionality will be limited.");
            }

            var vparam = $@"{dir}\Data0.bdt";
            // Load loose params if they exist
            if (loose && File.Exists($@"{mod}\\param\gameparam\gameparam_dlc2.parambnd.dcx"))
            {
                LoadParamsDS3FromFile($@"{mod}\param\gameparam\gameparam_dlc2.parambnd.dcx", true);
            }
            else
            {
                var param = $@"{mod}\Data0.bdt";
                if (!File.Exists(param))
                {
                    param = vparam;
                }
                LoadParamsDS3FromFile(param, false);
            }
        }
        private void LoadVParamsDS3()
        {
            LoadParamsDS3FromFile($@"{AssetLocator.GameRootDirectory}\Data0.bdt", false);
        }
        private void LoadParamsDS3FromFile(string path, bool isLoose)
        {
            BND4 lparamBnd = isLoose ? BND4.Read(path) : SFUtil.DecryptDS3Regulation(path);
            LoadParamFromBinder(lparamBnd, ref _params, out _paramVersion);
        }

        private void LoadParamsER(bool partial)
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;
            if (!File.Exists($@"{dir}\\regulation.bin"))
            {
                //MessageBox.Show("Could not find param file. Functionality will be limited.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                //return null;
                throw new FileNotFoundException("Could not find param file. Functionality will be limited.");
            }

            // Load params
            var param = $@"{mod}\regulation.bin";
            if (!File.Exists(param) || partial)
            {
                param = $@"{dir}\regulation.bin";
            }
            LoadParamsERFromFile(param);

            param = $@"{mod}\regulation.bin";
            if (partial && File.Exists(param))
            {
                BND4 pParamBnd = SFUtil.DecryptERRegulation(param);
                Dictionary<string, Param> cParamBank = new Dictionary<string, Param>();
                ulong v;
                LoadParamFromBinder(pParamBnd, ref cParamBank, out v, true);
                foreach (var pair in cParamBank)
                {
                    Param baseParam = _params[pair.Key];
                    foreach (var row in pair.Value.Rows)
                    {
                        Param.Row bRow = baseParam[row.ID];
                        if (bRow == null)
                        {
                            baseParam.AddRow(row);
                        }
                        else
                        {
                            bRow.Name = row.Name;
                            foreach (var field in bRow.Columns)
                            {
                                var cell = bRow[field];
                                cell.Value = row[field].Value;
                            }
                        }
                    }
                }
            }
        }
        private void LoadVParamsER()
        {
            LoadParamsERFromFile($@"{AssetLocator.GameRootDirectory}\regulation.bin");
        }
        private void LoadParamsERFromFile(string path)
        {
            LoadParamFromBinder(SFUtil.DecryptERRegulation(path), ref _params, out _paramVersion, true);
        }

        //Some returns and repetition, but it keeps all threading and loading-flags visible inside this method
        public static void ReloadParams(ProjectSettings settings, NewProjectOptions options)
        {
            // Steal assetlocator from PrimaryBank.
            AssetLocator locator = PrimaryBank.AssetLocator;

            _paramdefs = new Dictionary<string, PARAMDEF>();
            IsDefsLoaded = false;
            IsMetaLoaded = false;

            AuxBanks = new Dictionary<string, ParamBank>();

            PrimaryBank._params = new Dictionary<string, Param>();
            PrimaryBank.IsLoadingParams = true;

            CacheBank.ClearCaches();

            TaskManager.Run("PB:LoadParams", true, false, false, () =>
            {
                if (PrimaryBank.AssetLocator.Type != GameType.Undefined)
                {
                    List<(string, PARAMDEF)> defPairs = LoadParamdefs(locator);
                    IsDefsLoaded = true;
                    TaskManager.Run("PB:LoadParamMeta", true, false, false, () =>
                    {
                        LoadParamMeta(defPairs, locator);
                        IsMetaLoaded = true;
                    });
                }
                if (locator.Type == GameType.DemonsSouls)
                {
                    PrimaryBank.LoadParamsDES();
                }
                if (locator.Type == GameType.DarkSoulsPTDE)
                {
                    PrimaryBank.LoadParamsDS1();
                }
                if (locator.Type == GameType.DarkSoulsRemastered)
                {
                    PrimaryBank.LoadParamsDS1R();
                }
                if (locator.Type == GameType.DarkSoulsIISOTFS)
                {
                    PrimaryBank.LoadParamsDS2(settings.UseLooseParams);
                }
                if (locator.Type == GameType.DarkSoulsIII)
                {
                    PrimaryBank.LoadParamsDS3(settings.UseLooseParams);
                }
                if (locator.Type == GameType.Bloodborne || locator.Type == GameType.Sekiro)
                {
                    PrimaryBank.LoadParamsBBSekiro();
                }
                if (locator.Type == GameType.EldenRing)
                {
                    PrimaryBank.LoadParamsER(settings.PartialParams);
                }

                PrimaryBank.ClearParamDiffCaches();
                PrimaryBank.IsLoadingParams = false;

                VanillaBank.IsLoadingParams = true;
                VanillaBank._params = new Dictionary<string, Param>();
                TaskManager.Run("PB:LoadVParams", true, false, false, () =>
                {
                    if (locator.Type == GameType.DemonsSouls)
                    {
                        VanillaBank.LoadVParamsDES();
                    }
                    if (locator.Type == GameType.DarkSoulsPTDE)
                    {
                        VanillaBank.LoadVParamsDS1();
                    }
                    if (locator.Type == GameType.DarkSoulsRemastered)
                    {
                        VanillaBank.LoadVParamsDS1R();
                    }
                    if (locator.Type == GameType.DarkSoulsIISOTFS)
                    {
                        VanillaBank.LoadVParamsDS2(settings.UseLooseParams);
                    }
                    if (locator.Type == GameType.DarkSoulsIII)
                    {
                        VanillaBank.LoadVParamsDS3();
                    }
                    if (locator.Type == GameType.Bloodborne || locator.Type == GameType.Sekiro)
                    {
                        VanillaBank.LoadVParamsBBSekiro();
                    }
                    if (locator.Type == GameType.EldenRing)
                    {
                        VanillaBank.LoadVParamsER();
                    }
                    VanillaBank.IsLoadingParams = false;

                    TaskManager.Run("PB:RefreshDirtyCache", true, false, false, () => PrimaryBank.RefreshParamDiffCaches());
                });

                if (options != null)
                {
                    if (options.loadDefaultNames)
                    {
                        try
                        {
                            new Editor.ActionManager().ExecuteAction(PrimaryBank.LoadParamDefaultNames());
                            PrimaryBank.SaveParams(settings.UseLooseParams);
                        }
                        catch
                        {
                            TaskManager.warningList.TryAdd($"ParamNameImportFail", $"Could not locate or apply name files for this game.");
                        }
                    }
                }
            });
        }
        public static void LoadAuxBank(string path, string looseDir, string enemyPath, ProjectSettings settings)
        {
            // Steal assetlocator
            AssetLocator locator = PrimaryBank.AssetLocator;
            ParamBank newBank = new ParamBank();
            newBank.SetAssetLocator(locator);
            newBank._params = new Dictionary<string, Param>();
            newBank.IsLoadingParams = true;
            if (locator.Type == GameType.EldenRing)
            {
                newBank.LoadParamsERFromFile(path);
            }
            else if (locator.Type == GameType.Sekiro)
            {
                newBank.LoadParamsBBSekiroFromFile(path);
            }
            else if (locator.Type == GameType.DarkSoulsIII)
            {
                newBank.LoadParamsDS3FromFile(path, path.Trim().ToLower().EndsWith(".dcx"));
            }
            else if (locator.Type == GameType.Bloodborne)
            {
                newBank.LoadParamsBBSekiroFromFile(path);
            }
            else if (locator.Type == GameType.DarkSoulsIISOTFS)
            {
                var looseParams = GetLooseParamsInDir(looseDir);
                newBank.LoadParamsDS2FromFile(looseParams, path, enemyPath, settings.UseLooseParams);
            }
            else if (locator.Type == GameType.DarkSoulsRemastered)
            {
                newBank.LoadParamsDS1RFromFile(path);
            }
            else if (locator.Type == GameType.DarkSoulsPTDE)
            {
                newBank.LoadParamsDS1FromFile(path);
            }
            else if (locator.Type == GameType.DemonsSouls)
            {
                newBank.LoadParamsDESFromFile(path);
            }
            newBank.ClearParamDiffCaches();
            newBank.IsLoadingParams = false;
            newBank.RefreshParamDiffCaches();
            AuxBanks[Path.GetFileName(Path.GetDirectoryName(path)).Replace(' ', '_')] = newBank;
        }


        public void ClearParamDiffCaches()
        {
            _vanillaDiffCache = new Dictionary<string, HashSet<int>>();
            _primaryDiffCache = new Dictionary<string, HashSet<int>>();
            foreach (string param in _params.Keys)
            {
                _vanillaDiffCache.Add(param, new HashSet<int>());
                _primaryDiffCache.Add(param, new HashSet<int>());
            }
        }
        public void RefreshParamDiffCaches()
        {
            if (this != VanillaBank)
                _vanillaDiffCache = GetParamDiff(VanillaBank);
            if (this != PrimaryBank)
                _primaryDiffCache = GetParamDiff(PrimaryBank);
        }
        private Dictionary<string, HashSet<int>> GetParamDiff(ParamBank otherBank)
        {
            if (IsLoadingParams || otherBank == null || otherBank.IsLoadingParams)
                return null;
            Dictionary<string, HashSet<int>> newCache = new Dictionary<string, HashSet<int>>();
            foreach (string param in _params.Keys)
            {
                HashSet<int> cache = new HashSet<int>();
                newCache.Add(param, cache);
                Param p = _params[param];
                if (!otherBank._params.ContainsKey(param))
                {
                    Console.WriteLine("Missing vanilla param "+param);
                    continue;
                }

                var rows = _params[param].Rows.OrderBy(r => r.ID).ToArray();
                var vrows = otherBank._params[param].Rows.OrderBy(r => r.ID).ToArray();

                var vanillaIndex = 0;
                int lastID = -1;
                ReadOnlySpan<Param.Row> lastVanillaRows = default;
                for (int i = 0; i < rows.Length; i++)
                {
                    int ID = rows[i].ID;
                    if (ID == lastID)
                    {
                        RefreshParamRowDiffCache(rows[i], lastVanillaRows, cache);
                    }
                    else
                    {
                        lastID = ID;
                        while (vanillaIndex < vrows.Length && vrows[vanillaIndex].ID < ID)
                            vanillaIndex++;
                        if (vanillaIndex >= vrows.Length)
                        {
                            RefreshParamRowDiffCache(rows[i], Span<Param.Row>.Empty, cache);
                        }
                        else
                        {
                            int count = 0;
                            while (vanillaIndex + count < vrows.Length && vrows[vanillaIndex + count].ID == ID)
                                count++;
                            lastVanillaRows = new ReadOnlySpan<Param.Row>(vrows, vanillaIndex, count);
                            RefreshParamRowDiffCache(rows[i], lastVanillaRows, cache);
                            vanillaIndex += count;
                        }
                    }
                }
            }
            return newCache;
        }
        private static void RefreshParamRowDiffCache(Param.Row row, ReadOnlySpan<Param.Row> otherBankRows, HashSet<int> cache)
        {
            if (IsChanged(row, otherBankRows))
                cache.Add(row.ID);
            else
                cache.Remove(row.ID);
        }

        public void RefreshParamRowVanillaDiff(Param.Row row, string param)
        {
            if (param == null)
                return;
            if (!VanillaBank.Params.ContainsKey(param) || VanillaDiffCache == null || !VanillaDiffCache.ContainsKey(param))
                return; // Don't try for now
            var otherBankRows = VanillaBank.Params[param].Rows.Where(cell => cell.ID == row.ID).ToArray();
            if (IsChanged(row, otherBankRows))
                VanillaDiffCache[param].Add(row.ID);
            else
                VanillaDiffCache[param].Remove(row.ID);
        }

        private static bool IsChanged(Param.Row row, ReadOnlySpan<Param.Row> vanillaRows)
        {
            //List<Param.Row> vanils = vanilla.Rows.Where(cell => cell.ID == row.ID).ToList();
            if (vanillaRows.Length == 0)
            {
                return true;
            }
            foreach (Param.Row vrow in vanillaRows)
            {
                if (ParamUtils.RowMatches(row, vrow))
                    return false;//if we find a matching vanilla row
            }
            return true;
        }

        public void SetAssetLocator(AssetLocator l)
        {
            AssetLocator = l;
            //ReloadParams();
        }

        private void SaveParamsDS1()
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;
            if (!File.Exists($@"{dir}\\param\GameParam\GameParam.parambnd"))
            {
                MessageBox.Show("Could not find DS1 param file. Cannot save.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Load params
            var param = $@"{mod}\param\GameParam\GameParam.parambnd";
            if (!File.Exists(param))
            {
                param = $@"{dir}\param\GameParam\GameParam.parambnd";
            }
            BND3 paramBnd = BND3.Read(param);

            // Replace params with edited ones
            foreach (var p in paramBnd.Files)
            {
                if (_params.ContainsKey(Path.GetFileNameWithoutExtension(p.Name)))
                {
                    p.Bytes = _params[Path.GetFileNameWithoutExtension(p.Name)].Write();
                }
            }
            Utils.WriteWithBackup(dir, mod, @"param\GameParam\GameParam.parambnd", paramBnd);

            // Drawparam
            if (Directory.Exists($@"{AssetLocator.GameRootDirectory}\param\DrawParam"))
            {
                foreach (var bnd in Directory.GetFiles($@"{AssetLocator.GameRootDirectory}\param\DrawParam", "*.parambnd"))
                {
                    paramBnd = BND3.Read(bnd);
                    foreach (var p in paramBnd.Files)
                    {
                        if (_params.ContainsKey(Path.GetFileNameWithoutExtension(p.Name)))
                        {
                            p.Bytes = _params[Path.GetFileNameWithoutExtension(p.Name)].Write();
                        }
                    }
                    Utils.WriteWithBackup(dir, mod, @$"param\DrawParam\{Path.GetFileName(bnd)}", paramBnd);
                }
            }
        }
        private void SaveParamsDS1R()
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;
            if (!File.Exists($@"{dir}\\param\GameParam\GameParam.parambnd.dcx"))
            {
                MessageBox.Show("Could not find DS1R param file. Cannot save.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Load params
            var param = $@"{mod}\param\GameParam\GameParam.parambnd.dcx";
            if (!File.Exists(param))
            {
                param = $@"{dir}\param\GameParam\GameParam.parambnd.dcx";
            }
            BND3 paramBnd = BND3.Read(param);

            // Replace params with edited ones
            foreach (var p in paramBnd.Files)
            {
                if (_params.ContainsKey(Path.GetFileNameWithoutExtension(p.Name)))
                {
                    p.Bytes = _params[Path.GetFileNameWithoutExtension(p.Name)].Write();
                }
            }
            Utils.WriteWithBackup(dir, mod, @"param\GameParam\GameParam.parambnd.dcx", paramBnd);

            // Drawparam
            if (Directory.Exists($@"{AssetLocator.GameRootDirectory}\param\DrawParam"))
            {
                foreach (var bnd in Directory.GetFiles($@"{AssetLocator.GameRootDirectory}\param\DrawParam", "*.parambnd.dcx"))
                {
                    paramBnd = BND3.Read(bnd);
                    foreach (var p in paramBnd.Files)
                    {
                        if (_params.ContainsKey(Path.GetFileNameWithoutExtension(p.Name)))
                        {
                            p.Bytes = _params[Path.GetFileNameWithoutExtension(p.Name)].Write();
                        }
                    }
                    Utils.WriteWithBackup(dir, mod, @$"param\DrawParam\{Path.GetFileName(bnd)}", paramBnd);
                }
            }
        }

        private void SaveParamsDS2(bool loose)
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;
            if (!File.Exists($@"{dir}\enc_regulation.bnd.dcx"))
            {
                MessageBox.Show("Could not find DS2 regulation file. Cannot save.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Load params
            var param = $@"{mod}\enc_regulation.bnd.dcx";
            BND4 paramBnd;
            if (!File.Exists(param))
            {
                // If there is no mod file, check the base file. Decrypt it if you have to.
                param = $@"{dir}\enc_regulation.bnd.dcx";
                if (!BND4.Is($@"{dir}\enc_regulation.bnd.dcx"))
                {
                    // Decrypt the file
                    paramBnd = SFUtil.DecryptDS2Regulation(param);

                    // Since the file is encrypted, check for a backup. If it has none, then make one and write a decrypted one.
                    if (!File.Exists($@"{param}.bak"))
                    {
                        File.Copy(param, $@"{param}.bak", true);
                        paramBnd.Write(param);
                    }
                }
                // No need to decrypt
                else
                {
                    paramBnd = BND4.Read(param);
                }
            }
            // Mod file exists, use that.
            else
            {
                paramBnd = BND4.Read(param);
            }

            // If params aren't loose, replace params with edited ones
            if (!loose)
            {
                // Replace params in paramBND, write remaining params loosely
                if (paramBnd.Files.Find(e => e.Name.EndsWith(".param")) == null)
                {
                    if (MessageBox.Show("It appears that you are trying to save params non-loosely with an \"enc_regulation.bnd\" that has previously been saved loosely." +
                        "\n\nWould you like to reinsert params into the bnd that were previously stripped out?", "DS2 de-loose param",
                        MessageBoxButtons.YesNo) == DialogResult.Yes)
                    {
                        param = $@"{dir}\enc_regulation.bnd.dcx";
                        if (!BND4.Is($@"{dir}\enc_regulation.bnd.dcx"))
                        {
                            // Decrypt the file
                            paramBnd = SFUtil.DecryptDS2Regulation(param);

                            // Since the file is encrypted, check for a backup. If it has none, then make one and write a decrypted one.
                            if (!File.Exists($@"{param}.bak"))
                            {
                                File.Copy(param, $@"{param}.bak", true);
                                paramBnd.Write(param);
                            }
                        }
                        else
                        {
                            paramBnd = BND4.Read(param);
                        }
                    }
                }

                foreach (var p in _params)
                {
                    var bnd = paramBnd.Files.Find(e => Path.GetFileNameWithoutExtension(e.Name) == p.Key);
                    if (bnd != null)
                    {
                        bnd.Bytes = p.Value.Write();
                    }
                    else
                    {
                        Utils.WriteWithBackup(dir, mod, $@"Param\{p.Key}.param", p.Value);
                    }
                }
            }
            else
            {
                // strip all the params from the regulation
                List<BinderFile> newFiles = new List<BinderFile>();
                foreach (var p in paramBnd.Files)
                {
                    if (!p.Name.ToUpper().Contains(".PARAM"))
                    {
                        newFiles.Add(p);
                    }
                }
                paramBnd.Files = newFiles;

                // Write all the params out loose
                foreach (var p in _params)
                {
                    Utils.WriteWithBackup(dir, mod, $@"Param\{p.Key}.param", p.Value);
                }

            }
            Utils.WriteWithBackup(dir, mod, @"enc_regulation.bnd.dcx", paramBnd);
        }

        private void SaveParamsDS3(bool loose)
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;
            if (!File.Exists($@"{dir}\Data0.bdt"))
            {
                MessageBox.Show("Could not find DS3 regulation file. Cannot save.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Load params
            var param = $@"{mod}\Data0.bdt";
            if (!File.Exists(param))
            {
                param = $@"{dir}\Data0.bdt";
            }
            BND4 paramBnd = SFUtil.DecryptDS3Regulation(param);

            // Replace params with edited ones
            foreach (var p in paramBnd.Files)
            {
                if (_params.ContainsKey(Path.GetFileNameWithoutExtension(p.Name)))
                {
                    p.Bytes = _params[Path.GetFileNameWithoutExtension(p.Name)].Write();
                }
            }

            // If not loose write out the new regulation
            if (!loose)
            {
                Utils.WriteWithBackup(dir, mod, @"Data0.bdt", paramBnd, GameType.DarkSoulsIII);
            }
            else
            {
                // Otherwise write them out as parambnds
                BND4 paramBND = new BND4
                {
                    BigEndian = false,
                    Compression = DCX.Type.DCX_DFLT_10000_44_9,
                    Extended = 0x04,
                    Unk04 = false,
                    Unk05 = false,
                    Format = Binder.Format.Compression | Binder.Format.Flag6 | Binder.Format.LongOffsets | Binder.Format.Names1,
                    Unicode = true,
                    Files = paramBnd.Files.Where(f => f.Name.EndsWith(".param")).ToList()
                };

                /*BND4 stayBND = new BND4
                {
                    BigEndian = false,
                    Compression = DCX.Type.DCX_DFLT_10000_44_9,
                    Extended = 0x04,
                    Unk04 = false,
                    Unk05 = false,
                    Format = Binder.Format.Compression | Binder.Format.Flag6 | Binder.Format.LongOffsets | Binder.Format.Names1,
                    Unicode = true,
                    Files = paramBnd.Files.Where(f => f.Name.EndsWith(".stayparam")).ToList()
                };*/

                Utils.WriteWithBackup(dir, mod, @"param\gameparam\gameparam_dlc2.parambnd.dcx", paramBND);
                //Utils.WriteWithBackup(dir, mod, @"param\stayparam\stayparam.parambnd.dcx", stayBND);
            }
        }

        private void SaveParamsBBSekiro()
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;
            if (!File.Exists($@"{dir}\\param\gameparam\gameparam.parambnd.dcx"))
            {
                MessageBox.Show("Could not find param file. Cannot save.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Load params
            var param = $@"{mod}\param\gameparam\gameparam.parambnd.dcx";
            if (!File.Exists(param))
            {
                param = $@"{dir}\param\gameparam\gameparam.parambnd.dcx";
            }
            BND4 paramBnd = BND4.Read(param);

            // Replace params with edited ones
            foreach (var p in paramBnd.Files)
            {
                if (_params.ContainsKey(Path.GetFileNameWithoutExtension(p.Name)))
                {
                    p.Bytes = _params[Path.GetFileNameWithoutExtension(p.Name)].Write();
                }
            }
            Utils.WriteWithBackup(dir, mod, @"param\gameparam\gameparam.parambnd.dcx", paramBnd);
        }
        private void SaveParamsDES()
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;

            string paramBinderName = GetDesGameparamName(mod);
            if (paramBinderName == "")
            {
                paramBinderName = GetDesGameparamName(dir);
            }

            // Load params
            var param = $@"{mod}\param\gameparam\{paramBinderName}";
            if (!File.Exists(param))
            {
                param = $@"{dir}\param\gameparam\{paramBinderName}";
            }

            if (!File.Exists(param))
            {
                MessageBox.Show("Could not find param file. Cannot save.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            BND3 paramBnd = BND3.Read(param);

            // Replace params with edited ones
            foreach (var p in paramBnd.Files)
            {
                if (_params.ContainsKey(Path.GetFileNameWithoutExtension(p.Name)))
                {
                    p.Bytes = _params[Path.GetFileNameWithoutExtension(p.Name)].Write();
                }
            }

            // Write all gameparam variations since we don't know which one the the game will use.
            // Compressed
            paramBnd.Compression = DCX.Type.DCX_EDGE;
            string naParamPath = $@"param\gameparam\gameparamna.parambnd.dcx";
            if (File.Exists($@"{dir}\{naParamPath}"))
            {
                Utils.WriteWithBackup(dir, mod, naParamPath, paramBnd);
            }
            Utils.WriteWithBackup(dir, mod, $@"param\gameparam\gameparam.parambnd.dcx", paramBnd);

            // Decompressed
            paramBnd.Compression = DCX.Type.None;
            naParamPath = $@"param\gameparam\gameparamna.parambnd";
            if (File.Exists($@"{dir}\{naParamPath}"))
            {
                Utils.WriteWithBackup(dir, mod, naParamPath, paramBnd);
            }
            Utils.WriteWithBackup(dir, mod, $@"param\gameparam\gameparam.parambnd", paramBnd);

            // Drawparam
            List<string> drawParambndPaths = new();
            if (Directory.Exists($@"{AssetLocator.GameRootDirectory}\param\drawparam"))
            {
                foreach (var bnd in Directory.GetFiles($@"{AssetLocator.GameRootDirectory}\param\drawparam", "*.parambnd.dcx"))
                {
                    drawParambndPaths.Add(bnd);
                }
                // Also save decompressed parambnds because DeS debug uses them.
                foreach (var bnd in Directory.GetFiles($@"{AssetLocator.GameRootDirectory}\param\drawparam", "*.parambnd"))
                {
                    drawParambndPaths.Add(bnd);
                }
                foreach (var bnd in drawParambndPaths)
                {
                    paramBnd = BND3.Read(bnd);
                    foreach (var p in paramBnd.Files)
                    {
                        if (_params.ContainsKey(Path.GetFileNameWithoutExtension(p.Name)))
                        {
                            p.Bytes = _params[Path.GetFileNameWithoutExtension(p.Name)].Write();
                        }
                    }
                    Utils.WriteWithBackup(dir, mod, @$"param\drawparam\{Path.GetFileName(bnd)}", paramBnd);
                }
            }
        }
        private void SaveParamsER(bool partial)
        {
            var dir = AssetLocator.GameRootDirectory;
            var mod = AssetLocator.GameModDirectory;
            if (!File.Exists($@"{dir}\\regulation.bin"))
            {
                MessageBox.Show("Could not find param file. Cannot save.", "", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Load params
            var param = $@"{mod}\regulation.bin";
            if (!File.Exists(param) || _pendingUpgrade)
            {
                param = $@"{dir}\regulation.bin";
            }
            BND4 paramBnd = SFUtil.DecryptERRegulation(param);

            // Replace params with edited ones
            foreach (var p in paramBnd.Files)
            {
                if (_params.ContainsKey(Path.GetFileNameWithoutExtension(p.Name)))
                {
                    Param paramFile = _params[Path.GetFileNameWithoutExtension(p.Name)];
                    IReadOnlyList<Param.Row> backup = paramFile.Rows;
                    List<Param.Row> changed = new List<Param.Row>();
                    if (partial)
                    {
                        TaskManager.WaitAll();//wait on dirtycache update
                        HashSet<int> dirtyCache = _vanillaDiffCache[Path.GetFileNameWithoutExtension(p.Name)];
                        foreach (Param.Row row in paramFile.Rows)
                        {
                            if (dirtyCache.Contains(row.ID))
                                changed.Add(row);
                        }
                        paramFile.Rows = changed;
                    }
                    p.Bytes = paramFile.Write();
                    paramFile.Rows = backup;
                }
            }
            Utils.WriteWithBackup(dir, mod, @"regulation.bin", paramBnd, GameType.EldenRing);
            _pendingUpgrade = false;
        }

        public void SaveParams(bool loose = false, bool partialParams = false)
        {
            if (_params == null)
            {
                return;
            }
            if (AssetLocator.Type == GameType.DarkSoulsPTDE)
            {
                SaveParamsDS1();
            }
            if (AssetLocator.Type == GameType.DarkSoulsRemastered)
            {
                SaveParamsDS1R();
            }
            if (AssetLocator.Type == GameType.DemonsSouls)
            {
                SaveParamsDES();
            }
            if (AssetLocator.Type == GameType.DarkSoulsIISOTFS)
            {
                SaveParamsDS2(loose);
            }
            if (AssetLocator.Type == GameType.DarkSoulsIII)
            {
                SaveParamsDS3(loose);
            }
            if (AssetLocator.Type == GameType.Bloodborne || AssetLocator.Type == GameType.Sekiro)
            {
                SaveParamsBBSekiro();
            }
            if (AssetLocator.Type == GameType.EldenRing)
            {
                SaveParamsER(partialParams);
            }
        }

        public enum ParamUpgradeResult
        {
            Success = 0,
            RowConflictsFound = -1,
            OldRegulationNotFound = -2,
            OldRegulationVersionMismatch = -3,
        }

        private enum EditOperation
        {
            Add,
            Delete,
            Modify,
            NameChange,
            Match,
        }

        private static Param UpgradeParam(Param source, Param oldVanilla, Param newVanilla, HashSet<int> rowConflicts)
        {
            // Presorting this would make it easier, but we're trying to preserve order as much as possible
            // Unfortunately given that rows aren't guaranteed to be sorted and there can be duplicate IDs,
            // we try to respect the existing order and IDs as much as possible.

            // In order to assemble the final param, the param needs to know where to sort rows from given the
            // following rules:
            // 1. If a row with a given ID is unchanged from source to oldVanilla, we source from newVanilla
            // 2. If a row with a given ID is deleted from source compared to oldVanilla, we don't take any row
            // 3. If a row with a given ID is changed from source compared to oldVanilla, we source from source
            // 4. If a row has duplicate IDs, we treat them as if the rows were deduplicated and process them
            //    in the order they appear.

            // List of rows that are in source but not oldVanilla
            Dictionary<int, List<Param.Row>> addedRows = new Dictionary<int, List<Param.Row>>(source.Rows.Count);

            // List of rows in oldVanilla that aren't in source
            Dictionary<int, List<Param.Row>> deletedRows = new Dictionary<int, List<Param.Row>>(source.Rows.Count);

            // List of rows that are in source and oldVanilla, but are modified
            Dictionary<int, List<Param.Row>> modifiedRows = new Dictionary<int, List<Param.Row>>(source.Rows.Count);

            // List of rows that only had the name changed
            Dictionary<int, List<Param.Row>> renamedRows = new Dictionary<int, List<Param.Row>>(source.Rows.Count);

            // List of ordered edit operations for each ID
            Dictionary<int, List<EditOperation>> editOperations = new Dictionary<int, List<EditOperation>>(source.Rows.Count);

            // First off we go through source and everything starts as an added param
            foreach (var row in source.Rows)
            {
                if (!addedRows.ContainsKey(row.ID))
                    addedRows.Add(row.ID, new List<Param.Row>());
                addedRows[row.ID].Add(row);
            }

            // Next we go through oldVanilla to determine if a row is added, deleted, modified, or unmodified
            foreach (var row in oldVanilla.Rows)
            {
                // First off if the row did not exist in the source, it's deleted
                if (!addedRows.ContainsKey(row.ID))
                {
                    if (!deletedRows.ContainsKey(row.ID))
                        deletedRows.Add(row.ID, new List<Param.Row>());
                    deletedRows[row.ID].Add(row);
                    if (!editOperations.ContainsKey(row.ID))
                        editOperations.Add(row.ID, new List<EditOperation>());
                    editOperations[row.ID].Add(EditOperation.Delete);
                    continue;
                }

                // Otherwise the row exists in source. Time to classify it.
                var list = addedRows[row.ID];

                // First we see if we match the first target row. If so we can remove it.
                if (row.DataEquals(list[0]))
                {
                    var modrow = list[0];
                    list.RemoveAt(0);
                    if (list.Count == 0)
                        addedRows.Remove(row.ID);
                    if (!editOperations.ContainsKey(row.ID))
                        editOperations.Add(row.ID, new List<EditOperation>());

                    // See if the name was not updated
                    if ((modrow.Name == null && row.Name == null) ||
                        (modrow.Name != null && row.Name != null && modrow.Name == row.Name))
                    {
                        editOperations[row.ID].Add(EditOperation.Match);
                        continue;
                    }

                    // Name was updated
                    editOperations[row.ID].Add(EditOperation.NameChange);
                    if (!renamedRows.ContainsKey(row.ID))
                        renamedRows.Add(row.ID, new List<Param.Row>());
                    renamedRows[row.ID].Add(modrow);

                    continue;
                }

                // Otherwise it is modified
                if (!modifiedRows.ContainsKey(row.ID))
                    modifiedRows.Add(row.ID, new List<Param.Row>());
                modifiedRows[row.ID].Add(list[0]);
                list.RemoveAt(0);
                if (list.Count == 0)
                    addedRows.Remove(row.ID);
                if (!editOperations.ContainsKey(row.ID))
                    editOperations.Add(row.ID, new List<EditOperation>());
                editOperations[row.ID].Add(EditOperation.Modify);
            }

            // Mark all remaining rows as added
            foreach (var entry in addedRows)
            {
                if (!editOperations.ContainsKey(entry.Key))
                    editOperations.Add(entry.Key, new List<EditOperation>());
                foreach (var k in editOperations.Values)
                    editOperations[entry.Key].Add(EditOperation.Add);
            }

            Param dest = new Param(newVanilla);

            // Now try to build the destination from the new regulation with the edit operations in mind
            var pendingAdds = addedRows.Keys.OrderBy(e => e).ToArray();
            int currPendingAdd = 0;
            int lastID = 0;
            foreach (var row in newVanilla.Rows)
            {
                // See if we have any pending adds we can slot in
                while (currPendingAdd < pendingAdds.Length &&
                       pendingAdds[currPendingAdd] >= lastID &&
                       pendingAdds[currPendingAdd] < row.ID)
                {
                    if (!addedRows.ContainsKey(pendingAdds[currPendingAdd]))
                    {
                        currPendingAdd++;
                        continue;
                    }
                    foreach (var arow in addedRows[pendingAdds[currPendingAdd]])
                    {
                        dest.AddRow(new Param.Row(arow, dest));
                    }

                    addedRows.Remove(pendingAdds[currPendingAdd]);
                    editOperations.Remove(pendingAdds[currPendingAdd]);
                    currPendingAdd++;
                }

                lastID = row.ID;

                if (!editOperations.ContainsKey(row.ID))
                {
                    // No edit operations for this ID, so just add it (likely a new row in the update)
                    dest.AddRow(new Param.Row(row, dest));
                    continue;
                }

                // Pop the latest operation we need to do
                var operation = editOperations[row.ID][0];
                editOperations[row.ID].RemoveAt(0);
                if (editOperations[row.ID].Count == 0)
                    editOperations.Remove(row.ID);

                if (operation == EditOperation.Add)
                {
                    // Getting here means both the mod and the updated regulation added a row. Our current strategy is
                    // to overwrite the new vanilla row with the modded one and add to the conflict log to give the user
                    rowConflicts.Add(row.ID);
                    dest.AddRow(new Param.Row(addedRows[row.ID][0], dest));
                    addedRows[row.ID].RemoveAt(0);
                    if (addedRows[row.ID].Count == 0)
                        addedRows.Remove(row.ID);
                }
                else if (operation == EditOperation.Match)
                {
                    // Match means we inherit updated param
                    dest.AddRow(new Param.Row(row, dest));
                }
                else if (operation == EditOperation.Delete)
                {
                    // deleted means we don't add anything
                    deletedRows[row.ID].RemoveAt(0);
                    if (deletedRows[row.ID].Count == 0)
                        deletedRows.Remove(row.ID);
                }
                else if (operation == EditOperation.Modify)
                {
                    // Modified means we use the modded regulation's param
                    dest.AddRow(new Param.Row(modifiedRows[row.ID][0], dest));
                    modifiedRows[row.ID].RemoveAt(0);
                    if (modifiedRows[row.ID].Count == 0)
                        modifiedRows.Remove(row.ID);
                }
                else if (operation == EditOperation.NameChange)
                {
                    // Inherit name
                    var newRow = new Param.Row(row, dest);
                    newRow.Name = renamedRows[row.ID][0].Name;
                    dest.AddRow(newRow);
                    renamedRows[row.ID].RemoveAt(0);
                    if (renamedRows[row.ID].Count == 0)
                        renamedRows.Remove(row.ID);
                }
            }

            // Take care of any more pending adds
            for (; currPendingAdd < pendingAdds.Length; currPendingAdd++)
            {
                // If the pending add doesn't exist in the added rows list, it was a conflicting row
                if (!addedRows.ContainsKey(pendingAdds[currPendingAdd]))
                    continue;

                foreach (var arow in addedRows[pendingAdds[currPendingAdd]])
                {
                    dest.AddRow(new Param.Row(arow, dest));
                }

                addedRows.Remove(pendingAdds[currPendingAdd]);
                editOperations.Remove(pendingAdds[currPendingAdd]);
            }

            return dest;
        }

        // Param upgrade. Currently for Elden Ring only.
        public ParamUpgradeResult UpgradeRegulation(ParamBank vanillaBank, string oldVanillaParamPath,
            Dictionary<string, HashSet<int>> conflictingParams)
        {
            // First we need to load the old regulation
            if (!File.Exists(oldVanillaParamPath))
                return ParamUpgradeResult.OldRegulationNotFound;

            // Load old vanilla regulation
            BND4 oldVanillaParamBnd = SFUtil.DecryptERRegulation(oldVanillaParamPath);
            var oldVanillaParams = new Dictionary<string, Param>();
            ulong version;
            LoadParamFromBinder(oldVanillaParamBnd, ref oldVanillaParams, out version, true);
            if (version != ParamVersion)
                return ParamUpgradeResult.OldRegulationVersionMismatch;

            var updatedParams = new Dictionary<string, Param>();
            // Now we must diff everything to try and find changed/added rows for each param
            foreach (var k in vanillaBank.Params.Keys)
            {
                // If the param is completely new, just take it
                if (!oldVanillaParams.ContainsKey(k) || !Params.ContainsKey(k))
                {
                    updatedParams.Add(k, vanillaBank.Params[k]);
                    continue;
                }

                // Otherwise try to upgrade
                var conflicts = new HashSet<int>();
                var res = UpgradeParam(Params[k], oldVanillaParams[k], vanillaBank.Params[k], conflicts);
                updatedParams.Add(k, res);

                if (conflicts.Count > 0)
                    conflictingParams.Add(k, conflicts);
            }

            ulong oldVersion = _paramVersion;

            // Set new params
            _params = updatedParams;
            _paramVersion = VanillaBank.ParamVersion;
            _pendingUpgrade = true;

            // Refresh dirty cache
            CacheBank.ClearCaches();
            RefreshParamDiffCaches();

            return conflictingParams.Count > 0 ? ParamUpgradeResult.RowConflictsFound : ParamUpgradeResult.Success;
        }

        public (List<string>, List<string>) RunUpgradeEdits(ulong startVersion, ulong endVersion)
        {
            // Temporary data could be moved somewhere static
            (ulong, string, string)[] paramUpgradeTasks = new (ulong, string, string)[0];
            if (AssetLocator.Type == GameType.EldenRing)
            {
                // Note these all use modified as any unmodified row already matches the target. This only fails if a mod pre-empts fromsoft's exact change.
                paramUpgradeTasks = new (ulong, string, string)[]{
                    (10701000L, "1.07 - (SwordArtsParam) Move swordArtsType to swordArtsTypeNew", "param SwordArtsParam: modified: swordArtsTypeNew: = field swordArtsType;"),
                    (10701000L, "1.07 - (SwordArtsParam) Set swordArtsType to 0", "param SwordArtsParam: modified && !added: swordArtsType: = 0;"),
                    (10701000L, "1.07 - (AtkParam PC/NPC) Set added finalAttackDamageRate refs to -1", "param AtkParam_(Pc|Npc): modified && added: finalDamageRateId: = -1;"),
                    (10701000L, "1.07 - (AtkParam PC/NPC) Set not-added finalAttackDamageRate refs to vanilla", "param AtkParam_(Pc|Npc): modified && !added: finalDamageRateId: = vanillafield finalDamageRateId;"),
                    (10701000L, "1.07 - (GameSystemCommonParam) Set reserved_124 to Vanilla v1.07 values", "param GameSystemCommonParam: modified && !added: reserved_124: = vanillafield reserved_124;"),
                    (10701000L, "1.07 - (PlayerCommonParam) Set reserved41 to Vanilla v1.07 values", "param PlayerCommonParam: modified: reserved41: = vanillafield reserved41;"),
                    (10701000L, "1.07 - (AssetEnvironmentGeometryParam) Set unkR1 to Vanilla v1.07 values", "param AssetEnvironmentGeometryParam: modified && !added: unkR1: = vanillafield unkR1;"),
                    (10701000L, "1.07 - (AssetEnvironmentGeometryParam) Set unkR3 to Vanilla v1.07 values", "param AssetEnvironmentGeometryParam: modified && !added: unkR3: = vanillafield unkR3;"),
                    (10701000L, "1.07 - (AssetEnvironmentGeometryParam) Set unkR4 to Vanilla v1.07 values", "param AssetEnvironmentGeometryParam: modified && !added: unkR4: = vanillafield unkR4;"),
                    (10801000L, "1.08 - (BuddyParam) Set Unk1 to default value", "param BuddyParam: modified: Unk1: = 1410;"),
                    (10801000L, "1.08 - (BuddyParam) Set Unk2 to default value", "param BuddyParam: modified: Unk2: = 1420;"),
                    (10801000L, "1.08 - (BuddyParam) Set Unk11 to default value", "param BuddyParam: modified: Unk11: = 1400;"),
                    (10900000L, "1.09 - (GameSystemCommonParam) Set reserved_124 to Vanilla v1.09 values", "param GameSystemCommonParam: id 0: reserved_124: = vanillafield reserved_124;"),
                    //
                    (11001000L, "1.10 - (EquipParamWeapon) Set unk1 to Vanilla v1.10 values", "param EquipParamWeapon: modified && !added: unk1: = vanillafield unk1;"),
                    (11001000L, "1.10 - (ToughnessParam) Set unk1 to default value", "param ToughnessParam: added: unk1: = 1;"),
                    (11001000L, "1.10 - (ToughnessParam) Set unk1 to Vanilla v1.10 values", "param ToughnessParam: modified && !added: unk1: = vanillafield unk1;"),
                    (11001000L, "1.10 - (ToughnessParam) Set unk2 to Vanilla v1.10 values", "param ToughnessParam: modified && !added: unk2: = vanillafield unk2;"),
                };
            }

            List<string> performed = new List<string>();
            List<string> unperformed = new List<string>();

            bool hasFailed = false;
            foreach (var (version, task, command) in paramUpgradeTasks)
            {
                // Don't bother updating modified cache between edits
                if (version <= startVersion || version > endVersion)
                    continue;

                if (!hasFailed)
                {
                    try {
                        var (result, actions) = MassParamEditRegex.PerformMassEdit(this, command, null);
                        if (result.Type != MassEditResultType.SUCCESS)
                            hasFailed = true;
                    }
                    catch (Exception e)
                    {
                        hasFailed = true;
                    }
                }
                if (!hasFailed)
                    performed.Add(task);
                else
                    unperformed.Add(task);
            }
            return (performed, unperformed);
        }

        public string GetChrIDForEnemy(long enemyID)
        {
            var enemy = EnemyParam?[(int)enemyID];
            return enemy != null ? $@"{enemy.GetCellHandleOrThrow("chr_id").Value:D4}" : null;
        }

        public string GetKeyForParam(Param param)
        {
            foreach (KeyValuePair<string, Param> pair in Params)
            {
                if (param == pair.Value)
                    return pair.Key;
            }
            return null;
        }

        private double GetEquipLoad(int endurance)
        {
            Dictionary<int,double> equipDict = new Dictionary<int, double>()
            {
                { 1,45.0},
                { 2,45.0},
                { 3,45.0},
                { 4,45.0},
                { 5,45.0},
                { 6,45.0},
                { 7,45.0},
                { 8,45.0},
                { 9,46.6},
                { 10,48.2},
                { 11,49.8},
                { 12,51.4},
                { 13,52.9},
                { 14,54.5},
                { 15,56.1},
                { 16,57.7},
                { 17,59.3},
                { 18,60.9},
                { 19,62.5},
                { 20,64.1},
                { 21,65.6},
                { 22,67.2},
                { 23,68.8},
                { 24,70.4},
                { 25,72.0},
                { 26,73.0},
                { 27,74.1},
                { 28,75.2},
                { 29,76.4},
                { 30,77.6},
                { 31,78.9},
                { 32,80.2},
                { 33,81.5},
                { 34,82.8},
                { 35,84.1},
                { 36,85.4},
                { 37,86.8},
                { 38,88.1},
                { 39,89.5},
                { 40,90.9},
                { 41,92.3},
                { 42,93.7},
                { 43,95.1},
                { 44,96.5},
                { 45,97.9},
                { 46,99.4},
                { 47,100.8},
                { 48,102.2},
                { 49,103.7},
                { 50,105.2},
                { 51,106.6},
                { 52,108.1},
                { 53,109.6},
                { 54,111.0},
                { 55,112.5},
                { 56,114.0},
                { 57,115.5},
                { 58,117.0},
                { 59,118.5},
                { 60,120.0},
                { 61,121.0},
                { 62,122.1},
                { 63,123.1},
                { 64,124.1},
                { 65,125.1},
                { 66,126.2},
                { 67,127.2},
                { 68,128.2},
                { 69,129.2},
                { 70,130.3},
                { 71,131.3},
                { 72,132.3},
                { 73,133.3},
                { 74,134.4},
                { 75,135.4},
                { 76,136.4},
                { 77,137.4},
                { 78,138.5},
                { 79,139.5},
                { 80,140.5},
                { 81,141.5},
                { 82,142.6},
                { 83,143.6},
                { 84,144.6},
                { 85,145.6},
                { 86,146.7},
                { 87,147.7},
                { 88,148.7},
                { 89,149.7},
                { 90,150.8},
                { 91,151.8},
                { 92,152.8},
                { 93,153.8},
                { 94,154.9},
                { 95,155.9},
                { 96,156.9},
                { 97,157.9},
                { 98,159.0},
                { 99,160.0} 
            };

            return equipDict[endurance];
        }

        public Param GetParamFromName(string param)
        {
            foreach (KeyValuePair<string, Param> pair in Params)
            {
                if (param == pair.Key)
                    return pair.Value;
            }
            return null;
        }

        private static HashSet<int> EMPTYSET = new HashSet<int>();
        public HashSet<int> GetVanillaDiffRows(string param)
        {
            var allDiffs = VanillaDiffCache;
            if (allDiffs == null || !allDiffs.ContainsKey(param))
                return EMPTYSET;
            return allDiffs[param];
        }
        public HashSet<int> GetPrimaryDiffRows(string param)
        {
            var allDiffs = PrimaryDiffCache;
            if (allDiffs == null || !allDiffs.ContainsKey(param))
                return EMPTYSET;
            return allDiffs[param];
        }
    }
}
