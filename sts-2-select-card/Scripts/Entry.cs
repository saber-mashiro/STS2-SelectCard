using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;

namespace STS2SelectCard.Scripts;

[ModInitializer("Init")]
public partial class Entry
{
    private static bool _selectionUiBusy;
    private const string BackButtonScenePath = "res://scenes/ui/back_button.tscn";

    public static void Init()
    {
        var harmony = new Harmony("sts2selectcard.devconsole");
        harmony.PatchAll(typeof(Entry).Assembly);
        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);
        Log.Debug("STS2SelectCard initialized.");
    }

    [HarmonyPatch(typeof(DevConsole))]
    private static class DevConsolePatch
    {
        private static readonly System.Reflection.MethodInfo? RegisterCommandMethod = AccessTools.Method(typeof(DevConsole), "RegisterCommand");

        [HarmonyPostfix]
        [HarmonyPatch(MethodType.Constructor)]
        [HarmonyPatch(new[] { typeof(bool) })]
        private static void RegisterSelectCardCommand(DevConsole __instance)
        {
            if (RegisterCommandMethod == null)
            {
                Log.Error("STS2SelectCard could not find DevConsole.RegisterCommand.");
                return;
            }

            RegisterCommandMethod.Invoke(__instance, new object[] { new SelectCardConsoleCmd() });
        }
    }

    [HarmonyPatch(typeof(NPauseMenu))]
    private static class PauseMenuPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(NPauseMenu._Ready))]
        private static void InjectAddCardButton(NPauseMenu __instance)
        {
            if (!RunManager.Instance.IsInProgress)
            {
                return;
            }

            Control buttonContainer = __instance.GetNode<Control>("%ButtonContainer");
            if (buttonContainer.GetNodeOrNull<NPauseMenuButton>("AddCard") != null)
            {
                return;
            }

            string templateName = RunManager.Instance.NetService.Type == NetGameType.Client ? "Disconnect" : "SaveAndQuit";
            NPauseMenuButton templateButton = buttonContainer.GetNode<NPauseMenuButton>(templateName);
            NPauseMenuButton addCardButton = (NPauseMenuButton)templateButton.Duplicate();
            addCardButton.Name = "AddCard";
            addCardButton.Visible = true;
            addCardButton.GetNode("Label").Call("SetTextAutoSize", "添加卡牌");
            buttonContainer.AddChild(addCardButton);
            buttonContainer.MoveChild(addCardButton, buttonContainer.GetChildCount() - 1);
            addCardButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => TaskHelper.RunSafely(OpenCardSelectionFromPauseMenu())));
            WirePauseMenuFocus(buttonContainer);
        }
    }

    private sealed class SelectCardConsoleCmd : AbstractConsoleCmd
    {
        public override string CmdName => "selectcard";

        public override string Args => "[open]";

        public override string Description => "Opens an in-game card selection panel for the current character and adds the selected card directly to the deck.";

        public override bool IsNetworked => true;

        public override bool DebugOnly => false;

        public override CmdResult Process(Player? issuingPlayer, string[] args)
        {
            if (!RunManager.Instance.IsInProgress)
            {
                return new CmdResult(success: false, "A run is currently not in progress.");
            }

            if (issuingPlayer == null)
            {
                return new CmdResult(success: false, "No active player was found for this command.");
            }

            if (args.Length == 0)
            {
                return BuildOpenResult(issuingPlayer);
            }

            string subCommand = args[0].Trim().ToLowerInvariant();
            return subCommand switch
            {
                "open" => BuildOpenResult(issuingPlayer),
                _ => new CmdResult(success: false, "Usage: selectcard or selectcard open")
            };
        }

        public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
        {
            if (args.Length <= 1)
            {
                return CompleteArgument(new[] { "open" }, Array.Empty<string>(), args.FirstOrDefault() ?? "");
            }

            return base.GetArgumentCompletions(player, args);
        }

        private static CmdResult BuildOpenResult(Player player)
        {
            if (!TryBeginSelectionSession())
            {
                return new CmdResult(success: false, "The card selection panel is already open.");
            }

            IReadOnlyList<CardChoice> choices = GetChoices(player);
            if (choices.Count == 0)
            {
                EndSelectionSession();
                return new CmdResult(success: false, "No available cards were found for the current character.");
            }

            Task task = OpenSelectionPanel(player, choices);
            return new CmdResult(task, success: true, $"Opened selection panel with {choices.Count} cards for {player.Character.Title.GetFormattedText()}.");
        }

        internal static async Task OpenSelectionPanel(Player player, IReadOnlyList<CardChoice> choices)
        {
            List<CardModel> temporaryCards = choices
                .Select(choice => player.RunState.CreateCard(choice.CanonicalCard, player))
                .ToList();

            try
            {
                CardSelectorPrefs prefs = new CardSelectorPrefs(new LocString("gameplay_ui", "CHOOSE_CARD_HEADER"), 1);
                NSimpleCardSelectScreen selectionScreen = NSimpleCardSelectScreen.Create(temporaryCards, prefs);
                NOverlayStack.Instance?.Push(selectionScreen);
                await selectionScreen.ToSignal(selectionScreen.GetTree(), SceneTree.SignalName.ProcessFrame);
                AddCancelButton(selectionScreen);
                selectionScreen.GetNode("%BottomLabel").Call("SetTextAutoSize", "选择一张卡加入牌组");

                CardModel? selectedCard = (await selectionScreen.CardsSelected()).FirstOrDefault();
                if (selectedCard == null)
                {
                    Log.Info("STS2SelectCard card selection was canceled.");
                    return;
                }

                CardPileAddResult result = await CardPileCmd.Add(selectedCard, PileType.Deck);
                if (!result.success)
                {
                    Log.Warn($"STS2SelectCard failed to add '{selectedCard.Title}' [{selectedCard.Id.Entry}] to the deck.");
                    return;
                }

                Log.Info($"STS2SelectCard added '{result.cardAdded.Title}' [{result.cardAdded.Id.Entry}] to the deck from the selection panel.");
            }
            catch (TaskCanceledException)
            {
                Log.Info("STS2SelectCard card selection panel was closed before a card was chosen.");
            }
            finally
            {
                foreach (CardModel card in temporaryCards.Where(card => card.Pile == null && card.Owner != null && player.RunState.ContainsCard(card)))
                {
                    player.RunState.RemoveCard(card);
                }

                EndSelectionSession();
            }
        }

        internal static IReadOnlyList<CardChoice> GetChoices(Player player)
        {
            IReadOnlyDictionary<string, string> localizedTitles = CardTitleCache.GetTitles();
            return player.Character.CardPool
                .GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
                .GroupBy(card => card.Id.Entry, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    string cardId = group.Key;
                    string displayName = localizedTitles.TryGetValue(cardId, out string? title) && !string.IsNullOrWhiteSpace(title)
                        ? title
                        : group.First().TitleLocString.GetRawText();
                    return new CardChoice(cardId, displayName, group.First());
                })
                .OrderBy(choice => GetRaritySortOrder(choice.CanonicalCard.Rarity))
                .ThenBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(choice => choice.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int GetRaritySortOrder(CardRarity rarity)
        {
            return rarity switch
            {
                CardRarity.Basic => 0,
                CardRarity.Common => 1,
                CardRarity.Uncommon => 2,
                CardRarity.Rare => 3,
                CardRarity.Status => 4,
                CardRarity.Curse => 5,
                CardRarity.Event => 6,
                CardRarity.Token => 7,
                CardRarity.Quest => 8,
                CardRarity.Ancient => 9,
                _ => int.MaxValue
            };
        }
    }

    private sealed record CardChoice(string Id, string DisplayName, CardModel CanonicalCard);

    private static class CardTitleCache
    {
        private const string CardsLocPath = "res://localization/zhs/cards.json";
        private static Dictionary<string, string>? _titles;

        public static IReadOnlyDictionary<string, string> GetTitles()
        {
            return _titles ??= LoadTitles();
        }

        private static Dictionary<string, string> LoadTitles()
        {
            Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using Godot.FileAccess? file = Godot.FileAccess.Open(CardsLocPath, Godot.FileAccess.ModeFlags.Read);
            if (file == null)
            {
                Log.Warn($"STS2SelectCard could not open localization file at '{CardsLocPath}'.");
                return titles;
            }

            string json = file.GetAsText();
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (!property.Name.EndsWith(".title", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string cardId = property.Name[..^".title".Length];
                    string title = property.Value.GetString() ?? cardId;
                    titles[cardId.ToUpperInvariant()] = title;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"STS2SelectCard failed to parse '{CardsLocPath}': {ex}");
            }

            return titles;
        }
    }

    private static void WirePauseMenuFocus(Control buttonContainer)
    {
        List<NPauseMenuButton> buttons = buttonContainer.GetChildren()
            .OfType<NPauseMenuButton>()
            .Where(button => button.Visible)
            .ToList();

        for (int i = 0; i < buttons.Count; i++)
        {
            NPauseMenuButton button = buttons[i];
            button.FocusNeighborLeft = button.GetPath();
            button.FocusNeighborRight = button.GetPath();
            button.FocusNeighborTop = i > 0 ? buttons[i - 1].GetPath() : button.GetPath();
            button.FocusNeighborBottom = i < buttons.Count - 1 ? buttons[i + 1].GetPath() : button.GetPath();
        }
    }

    private static async Task OpenCardSelectionFromPauseMenu()
    {
        if (!TryBeginSelectionSession())
        {
            return;
        }

        NCapstoneContainer.Instance?.Close();
        CloseMapIfOpen();
        NRun.Instance?.GlobalUi?.TopBar?.Pause?.ToggleAnimState();
        if (NGame.Instance == null)
        {
            Log.Error("STS2SelectCard could not access NGame while opening from the pause menu.");
            EndSelectionSession();
            return;
        }

        await NGame.Instance.ToSignal(NGame.Instance.GetTree(), SceneTree.SignalName.ProcessFrame);

        Player? player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        if (player == null)
        {
            Log.Error("STS2SelectCard could not find the local player when opening from the pause menu.");
            EndSelectionSession();
            return;
        }

        IReadOnlyList<CardChoice> choices = SelectCardConsoleCmd.GetChoices(player);
        if (choices.Count == 0)
        {
            Log.Warn("STS2SelectCard found no available cards while opening from the pause menu.");
            EndSelectionSession();
            return;
        }

        await SelectCardConsoleCmd.OpenSelectionPanel(player, choices);
    }

    private static bool TryBeginSelectionSession()
    {
        if (_selectionUiBusy)
        {
            return false;
        }

        _selectionUiBusy = true;
        return true;
    }

    private static void EndSelectionSession()
    {
        _selectionUiBusy = false;
    }

    private static void CloseMapIfOpen()
    {
        NMapScreen? mapScreen = NMapScreen.Instance;
        if (mapScreen?.IsOpen ?? false)
        {
            mapScreen.Close(animateOut: false);
        }
    }

    private static void AddCancelButton(NSimpleCardSelectScreen selectionScreen)
    {
        if (selectionScreen.GetNodeOrNull<NBackButton>("AddCardCancel") != null)
        {
            return;
        }

        PackedScene? scene = ResourceLoader.Load<PackedScene>(BackButtonScenePath);
        if (scene == null)
        {
            Log.Warn($"STS2SelectCard could not load cancel button scene at '{BackButtonScenePath}'.");
            return;
        }

        NBackButton cancelButton = scene.Instantiate<NBackButton>();
        cancelButton.Name = "AddCardCancel";
        selectionScreen.AddChild(cancelButton);
        selectionScreen.MoveChild(cancelButton, selectionScreen.GetChildCount() - 1);
        cancelButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => NOverlayStack.Instance?.Remove(selectionScreen)));
        cancelButton.Enable();
    }
}

