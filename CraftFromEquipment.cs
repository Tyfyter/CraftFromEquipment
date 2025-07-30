using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.Cil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Localization;
using Terraria.Map;
using Terraria.ModLoader;
using Terraria.ModLoader.Default;
using Terraria.ModLoader.IO;
using Terraria.UI;

namespace CraftFromEquipment
{
	public class CraftFromEquipment : Mod {
		public static FastFieldInfo<ModAccessorySlotPlayer, Item[]> exAccessorySlot;
		public static FastFieldInfo<ModAccessorySlotPlayer, Item[]> exDyesAccessory;
		static LocalizedText enabledText;
		static LocalizedText disabledText;
		public override void Load() {
			exAccessorySlot = new("exAccessorySlot", BindingFlags.NonPublic);
			exDyesAccessory = new("exDyesAccessory", BindingFlags.NonPublic);
			On_Main.DrawLoadoutButtons += On_Main_DrawLoadoutButtons;
			On_Main.DrawInventory += On_Main_DrawInventory;
			try {
				IL_Main.DrawInventory += IL_Main_DrawInventory;
			} catch {
#if DEBUG
				throw;
#endif
			}
			enabledText = Language.GetOrRegister($"Mods.{Name}.Toggle.Enabled");
			disabledText = Language.GetOrRegister($"Mods.{Name}.Toggle.Disabled");
		}
		public override void Unload() {
			exAccessorySlot = null;
			exDyesAccessory = null;
			enabledText = null;
			disabledText = null;
		}
		Rectangle buttonPosition;
		private void On_Main_DrawLoadoutButtons(On_Main.orig_DrawLoadoutButtons orig, int inventoryTop, bool demonHeartSlotAvailable, bool masterModeSlotAvailable) {
			orig(inventoryTop, demonHeartSlotAvailable, masterModeSlotAvailable);
			int size = (int)(22 * Main.inventoryScale);
			buttonPosition = new(Main.screenWidth + (int)(58 * Main.inventoryScale) - 64 - 28 - size + 2, inventoryTop - size + 8, size, size);
		}
		private void On_Main_DrawInventory(On_Main.orig_DrawInventory orig, Main self) {
			orig(self);
			if (Main.EquipPage != 0) return;
			ref bool enabled = ref CraftFromEquipsPlayer.localPlayer.enabled;
			Texture2D texture = TextureAssets.HbLock[enabled ? 1 : 0].Value;
			Main.spriteBatch.Draw(texture, buttonPosition, new Rectangle(0, 0, 22, 22), Color.White);
			if (buttonPosition.Contains(Main.MouseScreen.ToPoint()) && !PlayerInput.IgnoreMouseInterface) {
				Main.spriteBatch.Draw(texture, buttonPosition, new Rectangle(26, 0, 22, 22), Color.White);
				Main.LocalPlayer.mouseInterface = true;
				Main.instance.MouseText(enabled ? enabledText.Value : disabledText.Value, 0, 0);
				Main.mouseText = true;
				if (Main.mouseLeft && Main.mouseLeftRelease) {
					enabled = !enabled;
					Recipe.FindRecipes();
					SoundEngine.PlaySound(SoundID.MenuTick);
				}
			}
		}
		void IL_Main_DrawInventory(ILContext il) {
			ILCursor cur = new(il);
			ILLabel skip = default;
			cur.GotoNext(i => i.MatchCall<Main>("DrawLoadoutButtons"));
			cur.GotoNext(MoveType.After,
				i => i.MatchCall<PlayerInput>("get_IgnoreMouseInterface"),
				i => i.MatchBrtrue(out skip),
				i => i.MatchLdcI4(1),
				i => i.MatchStsfld<Main>(nameof(Main.armorHide))
			);
			cur.Index -= 2;
			cur.EmitDelegate(() => buttonPosition.Contains(Main.MouseScreen.ToPoint()));
			cur.EmitBrtrue(skip);
		}
	}
	public class CraftFromEquipsPlayer : ModPlayer {
		public bool enabled = true;
		public static CraftFromEquipsPlayer localPlayer;
		public override void OnEnterWorld() {
			localPlayer = Main.LocalPlayer.GetModPlayer<CraftFromEquipsPlayer>();
		}
		public override void Unload() {
			localPlayer = null;
		}
		public override IEnumerable<Item> AddMaterialsForCrafting(out ItemConsumedCallback itemConsumedCallback) {
			itemConsumedCallback = (item, _) => {
				if (item.stack <= 0) item.TurnToAir(true);
			};
			if (!enabled) return [];
			ModAccessorySlotPlayer moddedSlots = Player.GetModPlayer<ModAccessorySlotPlayer>();
			return Player.armor.Concat(Player.dye).Concat(CraftFromEquipment.exAccessorySlot.GetValue(moddedSlots)).Concat(CraftFromEquipment.exDyesAccessory.GetValue(moddedSlots));
		}
		public override void SaveData(TagCompound tag) {
			tag.Add(nameof(enabled), enabled);
		}
		public override void LoadData(TagCompound tag) {
			enabled = tag.Get<bool>(nameof(enabled));
		}
	}
	public class FastFieldInfo<TParent, T> {
		public readonly FieldInfo field;
		Func<TParent, T> getter;
		Action<TParent, T> setter;
		public FastFieldInfo(string name, BindingFlags bindingFlags, bool init = false) {
			field = typeof(TParent).GetField(name, bindingFlags | BindingFlags.Instance);
			if (field is null) throw new ArgumentException($"could not find {name} in type {typeof(TParent)} with flags {bindingFlags.ToString()}");
			if (init) {
				getter = CreateGetter();
				setter = CreateSetter();
			}
		}
		public FastFieldInfo(FieldInfo field, bool init = false) {
			this.field = field;
			if (init) {
				getter = CreateGetter();
				setter = CreateSetter();
			}
		}
		public T GetValue(TParent parent) {
			return (getter ??= CreateGetter())(parent);
		}
		public void SetValue(TParent parent, T value) {
			(setter ??= CreateSetter())(parent, value);
		}
		private Func<TParent, T> CreateGetter() {
			if (field.FieldType != typeof(T)) throw new InvalidOperationException($"type of {field.Name} does not match provided type {typeof(T)}");
			string methodName = field.ReflectedType.FullName + ".get_" + field.Name;
			DynamicMethod getterMethod = new DynamicMethod(methodName, typeof(T), new Type[] { typeof(TParent) }, true);
			ILGenerator gen = getterMethod.GetILGenerator();

			gen.Emit(OpCodes.Ldarg_0);
			gen.Emit(OpCodes.Ldfld, field);
			gen.Emit(OpCodes.Ret);

			return (Func<TParent, T>)getterMethod.CreateDelegate(typeof(Func<TParent, T>));
		}
		private Action<TParent, T> CreateSetter() {
			if (field.FieldType != typeof(T)) throw new InvalidOperationException($"type of {field.Name} does not match provided type {typeof(T)}");
			string methodName = field.ReflectedType.FullName + ".set_" + field.Name;
			DynamicMethod setterMethod = new DynamicMethod(methodName, null, new Type[] { typeof(TParent), typeof(T) }, true);
			ILGenerator gen = setterMethod.GetILGenerator();

			gen.Emit(OpCodes.Ldarg_0);
			gen.Emit(OpCodes.Ldarg_1);
			gen.Emit(OpCodes.Stfld, field);
			gen.Emit(OpCodes.Ret);

			return (Action<TParent, T>)setterMethod.CreateDelegate(typeof(Action<TParent, T>));
		}
	}
}