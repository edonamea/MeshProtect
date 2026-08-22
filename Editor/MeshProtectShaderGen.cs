#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MeshProtect
{
    /// <summary>
    /// Emits a complete, independently named lilToon custom shader family for one variant.
    ///
    /// Why a whole family per avatar rather than one shared shader: the hash constants and the
    /// program structure are compiled INTO the shader. That is the point - it puts them in
    /// bytecode instead of in a material asset anyone can open in a text editor - but it also
    /// means two avatars with different variants cannot share a shader. lilToon derives the whole
    /// family name from one line in lilCustomShaderDatas.lilblock and every .lilcontainer refers to
    /// it through the *LIL_SHADER_NAME* placeholder, so a copy of the folder with that one line
    /// changed is all it takes. Measured cost: ~3.5 s for the import, once per variant.
    ///
    /// The folder is keyed by variant id and lives OUTSIDE the per-bake output folder, so re-bakes
    /// reuse it and cost nothing, and a rolled-back bake cannot delete a shader that shipped
    /// materials still reference.
    /// </summary>
    public static class MeshProtectShaderGen
    {
        private const string MarkerFile = "generated.txt";

        /// <summary>
        /// Bumped whenever the EMITTER changes what it writes for the same variant. The marker
        /// signature is otherwise variant-only, and a variant never changes once rolled, so
        /// without this a change to the generated text - the EditorName namespace move, say -
        /// would never reach families generated before it. Grafts share the signature through
        /// VariantSignature and regenerate on the same bump.
        /// 2: EditorName binds the inspector as MeshProtect.MeshProtectInspector.
        /// </summary>
        private const int GeneratorFormat = 2;

        /// <summary>
        /// Templates ship as *.lilcontainer.txt. With the real extension Unity would compile the
        /// template folder into a complete, unused shader family in every project that has the
        /// package - about 60 shaders and a few seconds of import for nothing. A .txt suffix is
        /// invisible to lilToon's importer but still travels inside a .unitypackage, which a
        /// "~"-suffixed folder would not.
        /// </summary>
        private const string TemplateSuffix = ".lilcontainer.txt";

        /// <summary>
        /// Make a freshly written shader folder visible to Unity, synchronously, so Shader.Find
        /// works on the next line.
        ///
        /// Deliberately not AssetDatabase.Refresh(ForceSynchronousImport): that forces every
        /// out-of-date asset in the project through the importer, and calling it while an initial
        /// project import is still settling crashed the editor outright in testing. A plain
        /// Refresh to discover the files, then a recursive import scoped to our own folder, does
        /// the same job without touching anything else.
        /// </summary>
        public static void ImportGenerated(string folder)
        {
            AssetDatabase.Refresh();
            AssetDatabase.ImportAsset(folder,
                ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceSynchronousImport);
        }

        public static string ProbeShaderName(MeshProtectVariant variant, bool fakeShadow)
        {
            return "Hidden/" + variant.shaderName + (fakeShadow ? "/DecodeFakeShadowProbe" : "/DecodeProbe");
        }

        /// <summary>
        /// Is this avatar's family complete right now, and by how much is it short?
        ///
        /// Exposed so the BUILD can ask. Generation already checks, but it warns to the Console at
        /// the moment a password is made - which is not a moment anybody is watching, and never
        /// reaches the upload report, the one thing an author actually reads afterwards. An
        /// incomplete family is silent everywhere else: materials that need a missing shader are
        /// left unprotected one by one, each with its own warning about that material, and nothing
        /// says the cause is the same for all of them and is one button away.
        /// </summary>
        public static bool FamilyIsComplete(MeshProtectVariant variant, out int actual, out int expected)
        {
            actual = FamilyShaderCount(variant);
            try { expected = Directory.GetFiles(LocateTemplateFolder(), "*" + TemplateSuffix).Length; }
            catch { expected = 0; }
            return expected == 0 || actual >= expected;
        }

        /// <summary>
        /// The folder everything this tool generates lives in.
        ///
        /// Inside the plugin's own folder, so removing the plugin removes every trace of it in
        /// one stroke. It used to be a folder at the project root, and that shape has two costs
        /// that were paid in support rather than in code: an uninstall leaves an orphaned folder
        /// nobody can place, and a fresh install sprouts a root-level tree the author never asked
        /// for. The component's Output Folder still overrides this - and components saved by
        /// older versions carry the old root in that field, so nothing moves under an upgrade.
        ///
        /// The old root also remains the fallback for installs that do not live under Assets: a
        /// package resolved into Packages/ can be read-only, and even a writable one is wiped by
        /// its manager on update, which is no place for generated content.
        /// </summary>
        public static string OutputRoot(MeshProtectRoot settings)
        {
            if (settings != null && !string.IsNullOrEmpty(settings.outputFolder))
            {
                string chosen = settings.outputFolder.Trim().TrimEnd('/', '\\').Replace('\\', '/');

                // "Assets/_MeshProtect" is what every pre-1.0 component carries in this field -
                // the serialized default of the day, not a choice anybody made. It is honoured
                // exactly as long as it actually holds their generated content. When nothing is
                // there - a fresh install, or a project the author already cleaned - resolving it
                // would re-sprout the root-level folder this release exists to retire, and that
                // is precisely how it was caught: a leftover component on a cleaned avatar put
                // the folder straight back.
                if (!chosen.Equals("Assets/_MeshProtect", StringComparison.OrdinalIgnoreCase) ||
                    AssetDatabase.IsValidFolder("Assets/_MeshProtect"))
                    return chosen;
            }

            string plugin = PluginFolder();
            return plugin != null && plugin.StartsWith("Assets/", StringComparison.Ordinal)
                ? plugin + "/Generated"
                : "Assets/_MeshProtect";
        }

        /// <summary>
        /// Where the plugin itself is installed, resolved from its own script asset the same way
        /// LocateTemplateFolder is - a renamed or relocated install still answers correctly.
        /// </summary>
        private static string cachedPluginFolder;

        private static string PluginFolder()
        {
            if (cachedPluginFolder != null) return cachedPluginFolder;

            foreach (var guid in AssetDatabase.FindAssets("MeshProtectShaderGen t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/Editor/MeshProtectShaderGen.cs", StringComparison.Ordinal))
                    continue;

                string root = path.Substring(0, path.Length - "/Editor/MeshProtectShaderGen.cs".Length);

                // The real install carries the shipped templates; a stray copy of this script
                // somewhere else does not, and must not win the resolution.
                if (!Directory.Exists(root + "/Shaders/Templates")) continue;

                // Cached until the next domain reload - which is exactly when a moved or
                // reimported install would need re-resolving anyway. A miss is never cached: a
                // FindAssets answer taken mid-refresh must not become the session's answer.
                return cachedPluginFolder = root;
            }
            return null;
        }

        /// <summary>Where a variant's shader family lives.</summary>
        public static string FolderFor(MeshProtectRoot settings, MeshProtectVariant variant)
        {
            return $"{OutputRoot(settings)}/_Shaders/{variant.shaderName}";
        }

        /// <summary>
        /// Create the shader family if it is missing or stale. Returns true if anything was
        /// written, which the caller uses to decide whether an AssetDatabase refresh is needed.
        /// </summary>
        public static bool EnsureGenerated(MeshProtectRoot settings, MeshProtectVariant variant,
                                           out string folder, bool force = false)
        {
            folder = FolderFor(settings, variant);

            string signature = Signature(variant);
            string markerPath = Path.Combine(folder, MarkerFile);
            string source = LocateTemplateFolder();
            int expected = Directory.GetFiles(source, "*" + TemplateSuffix).Length;

            // The signature is not enough on its own, and the reason is worth being exact about:
            // it describes the VARIANT, and the variant is rolled once and then never changes.
            // Everything else that decides what is actually in this folder is outside it - which
            // version of this package wrote the templates, which lilToon compiled them, and whether
            // the import succeeded at all. A family generated by an older release therefore matched
            // its signature forever, was never rebuilt, and Rebuild Shader answered "already up to
            // date" while the family was missing shaders that release had never shipped. What that
            // looks like from the outside: a lilToon material lands on a shader that is not ours
            // and the upload stops, naming a material the author never touched.
            //
            // So presence is checked too, by counting. One container makes one shader, so the
            // family should hold at least as many as there are templates - and counting rather than
            // naming them keeps this from going stale the next time lilToon adds a variant.
            if (!force && File.Exists(markerPath) &&
                File.ReadAllText(markerPath).Trim() == signature &&
                FamilyShaderCount(variant) >= expected)
                return false;

            // Files only. The grafts of foreign shader families live in SUBFOLDERS of this
            // one, each carrying its own marker and signature, so a stale one announces itself -
            // but none of them survives a recursive delete. A family rebuild used to take every
            // prepared graft with it, and the one caller that does not re-prepare grafts
            // afterwards left materials pointing at shaders that no longer existed.
            if (Directory.Exists(folder))
                foreach (var stale in Directory.GetFiles(folder))
                    File.Delete(stale);
            Directory.CreateDirectory(folder);

            // The containers are generic: they never name the shader family directly, only through
            // the placeholder lilToon substitutes. They ship with a .txt suffix so Unity does not
            // compile the template folder into a second, unused shader family in every project.
            foreach (var file in Directory.GetFiles(source, "*" + TemplateSuffix))
            {
                string name = Path.GetFileName(file);
                name = name.Substring(0, name.Length - TemplateSuffix.Length) + ".lilcontainer";
                File.Copy(file, Path.Combine(folder, name), true);
            }

            File.WriteAllText(Path.Combine(folder, "lilCustomShaderDatas.lilblock"),
                              EmitDatas(variant));
            File.WriteAllText(Path.Combine(folder, "lilCustomShaderProperties.lilblock"),
                              EmitProperties(variant));
            File.WriteAllText(Path.Combine(folder, "lilCustomShaderInsert.lilblock"),
                              "#include \"custom_insert.hlsl\"\n");
            File.WriteAllText(Path.Combine(folder, "custom.hlsl"), EmitCustomHlsl(variant));
            File.WriteAllText(Path.Combine(folder, "custom_insert.hlsl"), EmitDecodeHlsl(variant));

            // Probes for the GPU check. They compile the generated custom_insert.hlsl on its own,
            // with no lilToon include chain, so the bake can verify THIS avatar's generated HLSL
            // against the C# interpreter instead of some shared reference implementation. They are
            // never referenced by a material, so they do not end up in the uploaded bundle.
            File.WriteAllText(Path.Combine(folder, "DecodeProbe.shader"), EmitProbe(variant, false));
            File.WriteAllText(Path.Combine(folder, "DecodeFakeShadowProbe.shader"), EmitProbe(variant, true));

            // Import here rather than leaving it to the caller, because the marker must not be
            // written until the shaders exist. It used to record "the files were written", which is
            // not the question anybody is asking of it: a container that failed to compile, or a
            // folder Unity never imported, left a marker saying the family was ready and every
            // later call believed it.
            ImportGenerated(folder);

            int actual = FamilyShaderCount(variant);
            if (actual < expected)
            {
                Debug.LogWarning(
                    $"[MeshProtect] The shader family '{variant.shaderName}' came out with {actual} " +
                    $"shaders where {expected} were written. Something in it did not compile - the " +
                    "Console will have the shader error above this. Materials that need a missing " +
                    "one cannot be protected and will be left alone, and this will be retried " +
                    "rather than remembered as done.");
                return true;
            }

            File.WriteAllText(markerPath, signature);

            return true;
        }

        /// <summary>
        /// How many compiled shaders carry this family's name, probes excluded.
        ///
        /// Counted off the shaders Unity actually has rather than off the files on disk: a
        /// container that failed to compile is still a file. The probes are excluded because they
        /// are emitted per family too and would inflate the count past a family that is short.
        /// </summary>
        private static int FamilyShaderCount(MeshProtectVariant variant)
        {
            string prefix = variant.shaderName + "/";
            string hidden = "Hidden/" + variant.shaderName + "/";
            string probe = ProbeShaderName(variant, false);
            string fakeShadowProbe = ProbeShaderName(variant, true);

            int count = 0;
            foreach (var info in ShaderUtil.GetAllShaderInfo())
            {
                if (info.name == probe || info.name == fakeShadowProbe) continue;
                if (!info.name.StartsWith(prefix, StringComparison.Ordinal) &&
                    !info.name.StartsWith(hidden, StringComparison.Ordinal)) continue;

                // A shader that failed to compile is still IN GetAllShaderInfo, with hasErrors set.
                // Counting it made this check answer "the family is complete" about a family whose
                // shaders cannot render anything - and the marker was then written, so every later
                // call agreed and Rebuild Shader said it was already up to date.
                //
                // This is the half of the bug that only shows on somebody else's machine. Whether a
                // container compiles depends on their lilToon version and on lilToon's own shader
                // settings, which it regenerates from the features the project's materials actually
                // use. A clean project with plain lilToon materials compiles every container; an
                // avatar carrying Cutout+Outline, Transparent+Outline, Gem and Fur does not
                // necessarily, and nothing here noticed the difference.
                if (info.hasErrors || !info.supported) continue;

                count++;
            }
            return count;
        }

        /// <summary>
        /// Content identity of a generated family. Anything that changes the emitted HLSL has to
        /// appear here, or a stale shader would survive a variant change and silently decode with
        /// the wrong program.
        ///
        /// Public because the foreign-shader graft caches on the same thing and must invalidate on
        /// the same changes; a second copy of this list would be a second thing to keep in step.
        /// </summary>
        public static string VariantSignature(MeshProtectVariant variant) => Signature(variant);

        private static string Signature(MeshProtectVariant variant)
        {
            var sb = new StringBuilder();
            sb.Append("gen=").Append(GeneratorFormat).Append(';');
            sb.Append("format=").Append(variant.format).Append(';');
            sb.Append("shader=").Append(variant.shaderName).Append(';');
            sb.Append("props=").Append(variant.bypassProperty).Append(',')
              .Append(variant.modeProperty).Append(',')
              .Append(string.Join(",", variant.digitProperties)).Append(',')
              .Append(variant.macProperty).Append(';');
            // The salt shapes the emitted check; the password behind it does not, which is what
            // keeps a password change from needing a shader rebuild.
            sb.Append("mac=").Append(variant.macSalt).Append(';');
            sb.Append("id=").Append(variant.idMulX).Append(',').Append(variant.idMulY).Append(',')
              .Append(variant.idAdd).Append(',').Append(variant.idShift).Append(',')
              .Append(variant.idSwapChannels).Append(';');
            sb.Append("order=").Append(string.Join(",", variant.digitBitOrder)).Append(';');
            sb.Append("coef=").Append(variant.coefficientXor).Append(',')
              .Append(variant.swapCoefficients).Append(';');
            sb.Append("ops=");
            foreach (var op in variant.ops)
                sb.Append(op.kind).Append(':').Append(op.constant).Append(':').Append(op.shift).Append('/');
            return sb.ToString();
        }

        /// <summary>Find the shipped template folder by locating one of our own scripts.</summary>
        private static string LocateTemplateFolder()
        {
            foreach (var guid in AssetDatabase.FindAssets("MeshProtectShaderGen t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/MeshProtectShaderGen.cs", StringComparison.Ordinal)) continue;

                // <package>/Editor/MeshProtectShaderGen.cs -> <package>/Shaders
                string packageRoot = Path.GetDirectoryName(Path.GetDirectoryName(path))?.Replace('\\', '/');
                string templates = packageRoot + "/Shaders/Templates";
                if (Directory.Exists(templates) &&
                    Directory.GetFiles(templates, "*" + TemplateSuffix).Length > 0)
                    return templates;
            }

            throw new InvalidOperationException(
                "[MeshProtect] Could not find the shipped shader templates. The package layout " +
                "must be <package>/Editor/*.cs next to <package>/Shaders/Templates/*" + TemplateSuffix + ".");
        }

        // ------------------------------------------------------------------ emitters

        private static string EmitDatas(MeshProtectVariant variant)
        {
            return $"ShaderName \"{variant.shaderName}\"\n" +
                   "EditorName \"MeshProtect.MeshProtectInspector\"\n";
        }

        private static string EmitProperties(MeshProtectVariant variant)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("        //----------------------------------------------------------------------------------------------------------------------");
            sb.AppendLine("        // MeshProtect (generated)");
            sb.AppendLine($"        [lilToggle] {variant.bypassProperty} (\"Bypass mesh protection\", Int) = 0");
            sb.AppendLine($"        {variant.modeProperty} (\"Displacement mode\", Float) = 0");
            for (int i = 0; i < variant.digitProperties.Length; i++)
                sb.AppendLine($"        {variant.digitProperties[i]} (\"Digit {i + 1}\", Float) = 0");
            sb.AppendLine($"        {variant.macProperty} (\"Check\", Vector) = (0,0,0,0)");
            return sb.ToString();
        }

        private static string EmitCustomHlsl(MeshProtectVariant variant)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// Generated by MeshProtect. Do not edit; re-generated on demand.");
            sb.AppendLine();
            sb.AppendLine("#define LIL_CUSTOM_PROPERTIES \\");
            sb.AppendLine($"    float {variant.bypassProperty}; \\");
            sb.AppendLine($"    float {variant.modeProperty}; \\");
            for (int i = 0; i < variant.digitProperties.Length; i++)
                sb.AppendLine($"    float {variant.digitProperties[i]}; \\");
            sb.AppendLine($"    float4 {variant.macProperty};");
            sb.AppendLine();
            sb.AppendLine("#define LIL_CUSTOM_TEXTURES");
            sb.AppendLine();
            // TEXCOORD0 carries the vertex identity, TEXCOORD6.x the displacement amplitude. Both
            // are needed in every pass that decodes, including ones that sample no textures.
            sb.AppendLine("#define LIL_REQUIRE_APP_NORMAL");
            sb.AppendLine("#define LIL_REQUIRE_APP_TANGENT");
            sb.AppendLine("#define LIL_REQUIRE_APP_TEXCOORD0");
            sb.AppendLine("#define LIL_REQUIRE_APP_TEXCOORD6");
            sb.AppendLine();
            sb.AppendLine("#define LIL_CUSTOM_VERT_COPY");
            sb.AppendLine();
            // Must operate on the macro parameter, not on input.positionOS: lil_common_vert.hlsl
            // calls this a second time with previousPositionOS for the motion vector pass.
            sb.AppendLine("#define LIL_CUSTOM_VERTEX_OS \\");
            sb.AppendLine("    { \\");
            sb.AppendLine("        float3 kpPositionOS = positionOS.xyz; \\");
            sb.AppendLine("        lilMeshProtectDecode(input.normalOS, input.tangentOS, input.uv0, input.uv6, kpPositionOS); \\");
            sb.AppendLine("        positionOS.xyz = kpPositionOS; \\");
            sb.AppendLine("    }");
            return sb.ToString();
        }

        /// <summary>
        /// The decode, as text. Public because the foreign-shader graft embeds the same text in a
        /// non-lilToon host: one emitter, two hosts. A second emitter would be a third interpreter
        /// of the op list - alongside this one and MeshProtectCipher - and free to drift from both,
        /// which produces an avatar no password reopens.
        /// </summary>
        public static string EmitDecodeHlsl(MeshProtectVariant variant)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#ifndef LIL_MESH_PROTECT_INCLUDED");
            sb.AppendLine("#define LIL_MESH_PROTECT_INCLUDED");
            sb.AppendLine();
            sb.AppendLine("// Generated by MeshProtect. Do not edit.");
            sb.AppendLine("// The structure and constants below are unique to this avatar; the matching C# side");
            sb.AppendLine("// interprets the same op list from MeshProtectVariant, so the two cannot drift apart.");
            sb.AppendLine();

            // ---- vertex identity ----
            string first = variant.idSwapChannels ? "uv0.y" : "uv0.x";
            string second = variant.idSwapChannels ? "uv0.x" : "uv0.y";
            sb.AppendLine("uint lilMPVertexId(float2 uv0)");
            sb.AppendLine("{");
            sb.AppendLine($"    uint x = asuint({first});");
            sb.AppendLine($"    uint y = asuint({second});");
            sb.AppendLine($"    uint h = x * {Hex(variant.idMulX)};");
            sb.AppendLine($"    h ^= (y * {Hex(variant.idMulY)}) + {Hex(variant.idAdd)};");
            sb.AppendLine($"    h ^= h >> {variant.idShift};");
            sb.AppendLine("    return h;");
            sb.AppendLine("}");
            sb.AppendLine();

            // ---- key packing ----
            // round() before the cast matters: an animator transition can hand over a blended value
            // mid-way, and truncation would turn 6.999 into 5. The clamp also absorbs the 0 the
            // shipped material carries.
            sb.AppendLine("uint lilMPKey()");
            sb.AppendLine("{");
            sb.AppendLine("    uint key = 0;");
            for (int i = 0; i < variant.digitProperties.Length; i++)
            {
                int shift = MeshProtectRoot.BitsPerDigit * variant.digitBitOrder[i];
                // Digits 1-8 land on 0-7 as they always have; "not entered" takes the spare eighth
                // pattern instead of colliding with the digit 1. This has to agree with
                // MeshProtectRoot.KeyNibble exactly - the GPU check compares the two.
                string raw = $"(uint)clamp(round({variant.digitProperties[i]}), 0.0, 8.0)";
                string term = $"({raw} == 0u ? {MeshProtectRoot.MaxDigit}u : {raw} - 1u)";
                sb.AppendLine(shift == 0 ? $"    key |= {term};"
                                         : $"    key |= {term} << {shift};");
            }
            sb.AppendLine("    return key;");
            sb.AppendLine("}");
            sb.AppendLine();

            // ---- the hash program ----
            sb.AppendLine("uint lilMPHash(uint vertexId, uint key)");
            sb.AppendLine("{");
            sb.AppendLine("    uint h = vertexId;");
            foreach (var op in variant.ops)
            {
                switch (op.kind)
                {
                    case MeshProtectHashOp.XorConst:
                        sb.AppendLine($"    h ^= {Hex(op.constant)};"); break;
                    case MeshProtectHashOp.AddConst:
                        sb.AppendLine($"    h += {Hex(op.constant)};"); break;
                    case MeshProtectHashOp.MulConst:
                        sb.AppendLine($"    h *= {Hex(op.constant)};"); break;
                    case MeshProtectHashOp.XorShiftRight:
                        sb.AppendLine($"    h ^= h >> {op.shift};"); break;
                    case MeshProtectHashOp.XorShiftLeft:
                        sb.AppendLine($"    h ^= h << {op.shift};"); break;
                    case MeshProtectHashOp.XorKeyMul:
                        sb.AppendLine($"    h ^= key * {Hex(op.constant)};"); break;
                    case MeshProtectHashOp.AddKeyXor:
                        sb.AppendLine($"    h += key ^ {Hex(op.constant)};"); break;
                    case MeshProtectHashOp.Rotate:
                        sb.AppendLine($"    h = (h << {op.shift}) | (h >> {32 - op.shift});"); break;
                    default:
                        throw new InvalidOperationException($"Unknown hash op kind {op.kind}.");
                }
            }
            sb.AppendLine("    return h;");
            sb.AppendLine("}");
            sb.AppendLine();

            // ---- coefficients ----
            // Square distribution, not a disc: normalising needs a sqrt, and sqrt is only 1-ULP
            // accurate on D3D11, which would cost the bit-exactness the self check relies on.
            sb.AppendLine("float2 lilMPCoefficients(uint h)");
            sb.AppendLine("{");
            sb.AppendLine($"    h ^= {Hex(variant.coefficientXor)};");
            sb.AppendLine("    int2 i = int2(h & 0xFFFFu, (h >> 16) & 0xFFFFu) - 32768;");
            sb.AppendLine(variant.swapCoefficients
                ? "    return float2(i.y, i.x) * (1.0 / 32768.0);"
                : "    return float2(i.x, i.y) * (1.0 / 32768.0);");
            sb.AppendLine("}");
            sb.AppendLine();

            // ---- password check ----
            // A wrong password must look exactly like no password: invisible. Showing the
            // scrambled mesh instead put an eyesore in front of everyone standing nearby every
            // time the owner mis-tapped the menu.
            sb.AppendLine("bool lilMPKeyOk(uint key)");
            sb.AppendLine("{");
            sb.AppendLine($"    uint expected = (uint)round({variant.macProperty}.x)");
            sb.AppendLine($"                  | ((uint)round({variant.macProperty}.y) << 16);");
            sb.AppendLine($"    return lilMPHash({Hex(variant.macSalt)}, key) == expected;");
            sb.AppendLine("}");
            sb.AppendLine();

            // ---- locked state ----
            // There is no separate locked test any more. It existed because zero and one collapsed
            // to the same key, so "nobody has entered anything" had to be read off the raw
            // properties - and it asked whether ANY position was zero, which a password shorter
            // than six digits legitimately has. Now that the key carries the zeros, a shipped
            // material's six of them are simply a key, and a wrong one: lilMPKeyOk turns it away
            // like any other. One test, and it is right for every length.

            // ---- decode ----
            sb.AppendLine("void lilMeshProtectDecode(float3 normalOS, float4 tangentOS, float2 uv0, float2 uv6, inout float3 positionOS)");
            sb.AppendLine("{");
            sb.AppendLine($"    if({variant.bypassProperty} > 0.5) return;");
            sb.AppendLine();
            // Collapse every vertex onto one point rather than showing the scrambled mesh. All
            // triangles become degenerate and rasterise nothing, so a locked avatar is simply
            // invisible - in the regular pass, the outline, the shadow caster and the fake shadow
            // alike, because they all route through here.
            //
            // This IS a "was the password right" test, and the comment here used to say it was not.
            // It is a one-bit oracle and there is no version of this that isn't one: the right
            // password has to make the avatar appear, so anybody turning the dials can already see
            // the answer. What the collapse buys is that a wrong password looks like no password
            // rather than like a scrambled mesh, which is a courtesy to everyone else in the room.
            sb.AppendLine("    uint key = lilMPKey();");
            sb.AppendLine("    if(!lilMPKeyOk(key))");
            sb.AppendLine("    {");
            sb.AppendLine("        positionOS = float3(0.0, 0.0, 0.0);");
            sb.AppendLine("        return;");
            sb.AppendLine("    }");
            sb.AppendLine();
            // Zero amplitude means the vertex was never displaced - it is shared with an
            // unprotected sub-mesh, or joint attenuation scaled it out. Decoding it would move a
            // correct vertex.
            sb.AppendLine("    float amplitude = uv6.x;");
            sb.AppendLine("    if(!(amplitude > 0.0)) return;");
            sb.AppendLine();
            sb.AppendLine("    float2 ab = lilMPCoefficients(lilMPHash(lilMPVertexId(uv0), key));");
            sb.AppendLine();
            sb.AppendLine($"    if({variant.modeProperty} > 0.5)");
            sb.AppendLine("    {");
            // Tangent and normal only. The bitangent would have to be rebuilt here as cross() of
            // the SKINNED basis, and cross(A*n, A*t) != A*cross(n, t) unless A is orthogonal, which
            // a blended skinning matrix is not. Measured on a real avatar, that third axis was the
            // entire source of the residual (up to 4.4% at extreme joint angles).
            sb.AppendLine("        positionOS -= tangentOS.xyz * (amplitude * ab.x) + normalOS * (amplitude * ab.y);");
            sb.AppendLine("    }");
            sb.AppendLine("    else");
            sb.AppendLine("    {");
            sb.AppendLine("        positionOS -= normalOS * (amplitude * ab.x);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();

            // ---- FakeShadow ----
            sb.AppendLine("// lilToon's optional FakeShadow pass has its own small vertex shader and, unlike the");
            sb.AppendLine("// regular/outline/fur passes, does not call LIL_CUSTOM_VERTEX_OS. Decode at its only");
            sb.AppendLine("// LIL_VERTEX_POSITION_INPUTS call instead. Without this the FakeShadow material projects");
            sb.AppendLine("// the still-encrypted mesh over the avatar as moving triangles even with the right key.");
            sb.AppendLine("// Scoped to LIL_FAKESHADOW: redefining the helper for the normal passes would decode twice.");
            sb.AppendLine("#if defined(LIL_FAKESHADOW)");
            sb.AppendLine("    #undef LIL_VERTEX_POSITION_INPUTS");
            sb.AppendLine("    #define LIL_VERTEX_POSITION_INPUTS(positionOS,o) \\");
            sb.AppendLine("        float3 kpFakeShadowPositionOS = (positionOS).xyz; \\");
            sb.AppendLine("        lilMeshProtectDecode(input.normalOS, input.tangentOS, input.uv0, input.uv6, kpFakeShadowPositionOS); \\");
            sb.AppendLine("        lilVertexPositionInputs o = lilGetVertexPositionInputs(kpFakeShadowPositionOS)");
            sb.AppendLine("#endif");
            sb.AppendLine();
            sb.AppendLine("#endif");
            return sb.ToString();
        }

        /// <summary>
        /// A test-only shader that runs this variant's generated decode on the GPU so the bake can
        /// compare it against the C# interpreter. It declares the properties itself rather than
        /// relying on lilToon's cbuffer, so custom_insert.hlsl is compiled exactly as shipped with
        /// no include chain in the way.
        ///
        /// The fakeShadow flavour exercises the separate integration route lilToon's [Optional]
        /// FakeShadow pass uses: that pass bypasses LIL_CUSTOM_VERTEX_OS and calls
        /// LIL_VERTEX_POSITION_INPUTS directly, so the ordinary probe cannot catch a regression in
        /// it - and a regression there projects the still-encrypted mesh over the avatar.
        /// </summary>
        private static string EmitProbe(MeshProtectVariant variant, bool fakeShadow)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// Generated by MeshProtect. Verification only; never referenced by a material,");
            sb.AppendLine("// so it does not reach the uploaded bundle.");
            sb.AppendLine($"Shader \"{ProbeShaderName(variant, fakeShadow)}\"");
            sb.AppendLine("{");
            sb.AppendLine("    SubShader");
            sb.AppendLine("    {");
            sb.AppendLine("        Cull Off ZWrite Off ZTest Always");
            sb.AppendLine("        Pass");
            sb.AppendLine("        {");
            sb.AppendLine("            CGPROGRAM");
            sb.AppendLine("            #pragma vertex vert");
            sb.AppendLine("            #pragma fragment frag");
            sb.AppendLine("            #include \"UnityCG.cginc\"");
            sb.AppendLine();
            sb.AppendLine($"            float {variant.bypassProperty};");
            sb.AppendLine($"            float {variant.modeProperty};");
            foreach (var property in variant.digitProperties)
                sb.AppendLine($"            float {property};");
            sb.AppendLine($"            float4 {variant.macProperty};");
            sb.AppendLine();

            if (fakeShadow)
            {
                sb.AppendLine("            // Minimal stand-ins for the lilToon pipeline symbols the FakeShadow");
                sb.AppendLine("            // override touches. The macro call below is the same one made by");
                sb.AppendLine("            // lil_pass_forward_fakeshadow.hlsl.");
                sb.AppendLine("            struct lilVertexPositionInputs { float3 positionOS; };");
                sb.AppendLine("            lilVertexPositionInputs lilGetVertexPositionInputs(float3 p)");
                sb.AppendLine("            {");
                sb.AppendLine("                lilVertexPositionInputs o;");
                sb.AppendLine("                o.positionOS = p;");
                sb.AppendLine("                return o;");
                sb.AppendLine("            }");
                sb.AppendLine("            #define LIL_VERTEX_POSITION_INPUTS(positionOS,o) \\");
                sb.AppendLine("                lilVertexPositionInputs o = lilGetVertexPositionInputs((positionOS).xyz)");
                sb.AppendLine("            #define LIL_FAKESHADOW");
            }

            sb.AppendLine("            #include \"custom_insert.hlsl\"");
            sb.AppendLine();
            sb.AppendLine("            sampler2D _ProbeNormal;    // xyz = normalOS, w = tangentOS.w");
            sb.AppendLine("            sampler2D _ProbeTangent;   // xyz = tangentOS.xyz");
            sb.AppendLine("            // xy = uv0 (identity, hashed by its raw bits), zw = uv6 (x = amplitude).");
            sb.AppendLine("            // An RGBAFloat texture with point filtering round-trips fp32 bit for bit,");
            sb.AppendLine("            // which this check depends on: one altered mantissa bit changes the identity.");
            sb.AppendLine("            sampler2D _ProbeUV;");
            sb.AppendLine();
            sb.AppendLine("            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };");

            if (fakeShadow)
            {
                sb.AppendLine("            struct kpFakeShadowAppdata");
                sb.AppendLine("            {");
                sb.AppendLine("                float4 positionOS;");
                sb.AppendLine("                float3 normalOS;");
                sb.AppendLine("                float4 tangentOS;");
                sb.AppendLine("                float2 uv0;");
                sb.AppendLine("                float2 uv6;");
                sb.AppendLine("            };");
            }

            sb.AppendLine();
            sb.AppendLine("            v2f vert(appdata_img v)");
            sb.AppendLine("            {");
            sb.AppendLine("                v2f o;");
            sb.AppendLine("                o.pos = UnityObjectToClipPos(v.vertex);");
            sb.AppendLine("                o.uv = v.texcoord;");
            sb.AppendLine("                return o;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            float4 frag(v2f i) : SV_Target");
            sb.AppendLine("            {");
            sb.AppendLine("                float4 n = tex2D(_ProbeNormal, i.uv);");
            sb.AppendLine("                float4 t = tex2D(_ProbeTangent, i.uv);");
            sb.AppendLine("                float4 uv = tex2D(_ProbeUV, i.uv);");
            sb.AppendLine();

            if (fakeShadow)
            {
                sb.AppendLine("                kpFakeShadowAppdata input;");
                sb.AppendLine("                input.positionOS = float4(0, 0, 0, 1);");
                sb.AppendLine("                input.normalOS = n.xyz;");
                sb.AppendLine("                input.tangentOS = float4(t.xyz, n.w);");
                sb.AppendLine("                input.uv0 = uv.xy;");
                sb.AppendLine("                input.uv6 = uv.zw;");
                sb.AppendLine();
                sb.AppendLine("                LIL_VERTEX_POSITION_INPUTS(input.positionOS, vertexInput);");
                sb.AppendLine("                return float4(-vertexInput.positionOS, 1);");
            }
            else
            {
                sb.AppendLine("                // The function subtracts the displacement, so starting from zero returns -d.");
                sb.AppendLine("                float3 p = float3(0, 0, 0);");
                sb.AppendLine("                lilMeshProtectDecode(n.xyz, float4(t.xyz, n.w), uv.xy, uv.zw, p);");
                sb.AppendLine("                return float4(-p, 1);");
            }

            sb.AppendLine("            }");
            sb.AppendLine("            ENDCG");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Hex(int value) => "0x" + unchecked((uint)value).ToString("x8") + "u";
    }
}
#endif
