using System;
using UnityEngine;

namespace MeshProtect
{
    /// <summary>
    /// One step of the generated hash. Both the C# baker and the generated HLSL are driven from
    /// the same op list, which is the only reason the two halves cannot drift apart: there is no
    /// hand-written copy of the algorithm on either side, just two interpreters of one program.
    /// </summary>
    [Serializable]
    public struct MeshProtectHashOp
    {
        public const int XorConst = 0;
        public const int AddConst = 1;
        public const int MulConst = 2;   // constant is forced odd, so the step stays invertible
        public const int XorShiftRight = 3;
        public const int XorShiftLeft = 4;
        public const int XorKeyMul = 5;  // h ^= key * C
        public const int AddKeyXor = 6;  // h += key ^ C
        public const int Rotate = 7;

        public int kind;
        public int constant;   // reinterpreted as uint
        public int shift;

        public uint Constant => unchecked((uint)constant);
    }

    /// <summary>
    /// Everything that differs between one build's protection and the next.
    ///
    /// This is the moat. A fixed algorithm means one person writes one unpacker and every avatar
    /// using the tool falls at once - which is exactly what happened to the previous generation of
    /// mesh protection. Here the hash structure, its constants, the shader family name and every
    /// property and parameter name are generated per avatar, and the constants are compiled into
    /// the shader rather than sitting in a readable material asset. An attacker has to reverse
    /// each avatar's shader bytecode on its own; nothing carries over to the next one.
    ///
    /// Generated once, alongside the password, and then REUSED for every re-bake. Regenerating it
    /// would rename the expression parameters, and renamed parameters lose their saved values, so
    /// the owner would have to re-enter the password after every re-bake.
    /// </summary>
    [Serializable]
    public class MeshProtectVariant
    {
        /// <summary>Bumped when the generator or the interpreter changes shape.</summary>
        public const int CurrentFormat = 1;

        public int format = CurrentFormat;

        /// <summary>Short id, also the folder name of the generated shader family.</summary>
        public string id = "";

        /// <summary>lilToon shader family name, e.g. "MPa3f19c22".</summary>
        public string shaderName = "";

        // ---- generated identifiers ----

        public string bypassProperty = "";
        public string modeProperty = "";
        public string[] digitProperties = new string[0];   // one per password digit
        public string[] parameterNames = new string[0];    // menu input, one Int per digit, UNSYNCED

        /// <summary>
        /// The synced transport: four bools per digit, 24 in total.
        ///
        /// A password is 18 bits of information (eight choices per digit, six digits) and this
        /// spends 24 bits of VRChat's 256-bit sync budget to carry it.
        ///
        /// Three bits per digit is exactly enough for eight values and is what this used to spend,
        /// but it left no pattern meaning "not entered": a shipped material's zeroes read as the
        /// lowest digit, so one password would have arrived already unlocked. The fourth bit buys
        /// that state. The obvious encoding - one synced Int per digit - costs 48 bits
        /// for the same 18, and a real dressed-up avatar measured during development already used
        /// 218, so the obvious encoding simply did not fit. Unsynced parameters cost nothing
        /// (measured), so the menu writes those and a parameter driver packs them into these.
        /// </summary>
        public string[] bitNames = new string[0];

        /// <summary>
        /// Material property carrying a value derived from the password, which the shader
        /// recomputes and compares. A mismatch collapses the avatar, so a WRONG password looks the
        /// same as no password at all - invisible - instead of a scrambled mesh in everyone's face.
        ///
        /// It lives in the material rather than compiled into the shader on purpose. The shader's
        /// identity deliberately does not include the password, which is what makes changing a
        /// password instant instead of a shader rebuild; baking the check into the shader would
        /// undo that.
        ///
        /// Yes, this is an oracle: someone who has already reversed this avatar's hash program can
        /// now test a candidate password without rendering anything. That costs close to nothing.
        /// The key is 18 bits, and an attacker at that stage could already brute-force it by
        /// rendering and scoring which result looks like a person. What it buys is that a legitimate
        /// owner who fumbles the menu does not blind the room.
        /// </summary>
        public string macProperty = "";

        /// <summary>Per-variant input to the verification hash, so the check differs per avatar.</summary>
        public int macSalt;

        /// <summary>
        /// FX layer names, and the names of the assets generated into the avatar.
        ///
        /// Stored rather than derived because they ship: an AnimatorController's layer names and a
        /// ScriptableObject's name both travel inside the uploaded bundle. They used to say
        /// "<id>_pack" and "<id>_decode", which is a label describing what the layer does, next to
        /// a family id shared by every avatar this tool has ever produced. Now they are drawn from
        /// the same pattern-free pool as everything else, and kept here so a re-bake reproduces
        /// them.
        /// </summary>
        public string[] packLayerNames = new string[0];
        public string[] decodeLayerNames = new string[0];
        public string[] padLayerNames = new string[0];
        public string menuAssetName = "";
        public string[] digitMenuAssetNames = new string[0];
        public string rootMenuAssetName = "";

        /// <summary>Only used when the avatar's root menu is already full; see AttachSubMenu.</summary>
        public string wrapperMenuAssetName = "";
        public string parametersAssetName = "";
        public string controllerAssetName = "";

        // ---- vertex identity ----

        public int idMulX;
        public int idMulY;
        public int idAdd;
        public int idShift;
        public bool idSwapChannels;

        // ---- key packing: which 3-bit slot each digit occupies ----

        public int[] digitBitOrder = new int[0];

        // ---- the hash program ----

        public MeshProtectHashOp[] ops = new MeshProtectHashOp[0];

        // ---- coefficient extraction ----

        public int coefficientXor;
        public bool swapCoefficients;

        /// <summary>Bits per digit. Mirrors MeshProtectRoot.BitsPerDigit; kept here so the
        /// serialised model can validate itself without reaching into the component.</summary>
        public const int BitsPerDigitCount = 4;

        /// <summary>
        /// Each digit occupies its own nibble, so the slots have to be 0..n-1 with none repeated.
        /// A slot out of range shifts by 24 or more - at 8 that is a shift of 32, which C# masks
        /// back to zero and HLSL leaves undefined, so the two interpreters can disagree per GPU. A
        /// repeated slot is worse and quieter: two digits OR into one nibble and one position of
        /// the password stops mattering, while the generator's avalanche gate tries a single set of
        /// digits and need not notice. Shuffle cannot produce either, so only a damaged variant
        /// gets here - a hand-edited inspector, a merge conflict, an older serialised asset.
        /// </summary>
        private static bool IsPermutation(int[] order, int length)
        {
            var seen = new bool[length];
            foreach (int slot in order)
            {
                if (slot < 0 || slot >= length || seen[slot]) return false;
                seen[slot] = true;
            }
            return true;
        }

        public bool IsValid(int passwordLength)
        {
            return format == CurrentFormat
                && !string.IsNullOrEmpty(id)
                && !string.IsNullOrEmpty(shaderName)
                && !string.IsNullOrEmpty(bypassProperty)
                && !string.IsNullOrEmpty(modeProperty)
                && digitProperties != null && digitProperties.Length == passwordLength
                && parameterNames != null && parameterNames.Length == passwordLength
                && bitNames != null && bitNames.Length == passwordLength * BitsPerDigitCount
                && !string.IsNullOrEmpty(macProperty)
                && packLayerNames != null && packLayerNames.Length == passwordLength
                && decodeLayerNames != null && decodeLayerNames.Length == passwordLength
                && !string.IsNullOrEmpty(rootMenuAssetName)
                && !string.IsNullOrEmpty(wrapperMenuAssetName)
                // The rest of the generated names, which the build uses as asset paths and array
                // indices without asking again. Checking some of them and not others let a variant
                // pass here and then fail mid-build with IndexOutOfRange, or write an asset called
                // ".asset", which is a much worse place to find out.
                && !string.IsNullOrEmpty(menuAssetName)
                && !string.IsNullOrEmpty(parametersAssetName)
                && !string.IsNullOrEmpty(controllerAssetName)
                && digitMenuAssetNames != null && digitMenuAssetNames.Length == passwordLength
                && padLayerNames != null && padLayerNames.Length > 0
                && digitBitOrder != null && digitBitOrder.Length == passwordLength
                && IsPermutation(digitBitOrder, passwordLength)
                && ops != null && ops.Length > 0;
        }
    }
}
