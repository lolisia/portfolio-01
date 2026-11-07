using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace aisilol.Game
{
    // 체력/경험치 바, 강화 자동 선택 체크박스, 레벨업 선택지, 결과 화면. Canvas 아래에 코드로 구성
    public sealed class GameHud
    {
        private static readonly Color PanelColor = new(0, 0, 0, 0.75f);
        private static readonly Color ButtonColor = new(0.2f, 0.2f, 0.25f, 1);
        private static readonly Color BarBackgroundColor = new(0, 0, 0, 0.6f);
        private static readonly Color CheckColor = new(0.3f, 0.9f, 0.4f, 1);

        private readonly RectTransform _healthFill;
        private readonly RectTransform _experienceFill;
        private readonly TextMeshProUGUI _levelText;

        private readonly Button[] _choiceButtons;
        private readonly TextMeshProUGUI[] _choiceTexts;

        internal GameObject LevelUpPanel { get; }

        internal GameObject GameOverPanel { get; }

        internal IReadOnlyList<Button> ChoiceButtons => _choiceButtons;
        internal Toggle AutoSelectToggle { get; }
        internal TextMeshProUGUI ResultText { get; }

        public GameHud(Canvas canvas, int choiceCount, Action<int> onSelect, Action onRestart, bool autoSelect = false, Action<bool> onAutoSelectChanged = null)
        {
            var root = CreateRect("HUD", canvas.transform);
            Stretch(root);

            // 화면 위쪽 경험치 바, 그 아래 체력 바
            _experienceFill = CreateBar("ExperienceBar", root, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -6), new Vector2(0, 12), new Color(0.3f, 0.7f, 1));
            _healthFill = CreateBar("HealthBar", root, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -26), new Vector2(300, 14), new Color(0.9f, 0.2f, 0.2f));

            _levelText = CreateText("LevelText", root, 24, TextAlignmentOptions.TopRight);
            var levelRect = _levelText.rectTransform;
            levelRect.anchorMin = levelRect.anchorMax = levelRect.pivot = new Vector2(1, 1);
            levelRect.anchoredPosition = new Vector2(-16, -20);
            levelRect.sizeDelta = new Vector2(200, 40);

            // 레벨 아래 강화 자동 선택
            AutoSelectToggle = CreateToggle("AutoSelectToggle", root, new Vector2(-16, -64), "Auto Upgrade", autoSelect);
            if (onAutoSelectChanged != null)
                AutoSelectToggle.onValueChanged.AddListener(value => onAutoSelectChanged(value));

            // 레벨업 선택지
            LevelUpPanel = CreatePanel("LevelUpPanel", root, new Vector2(420, 120 + choiceCount * 80));
            CreateTitle(LevelUpPanel.transform, "LEVEL UP");

            _choiceButtons = new Button[choiceCount];
            _choiceTexts = new TextMeshProUGUI[choiceCount];
            for (var index = 0; index < choiceCount; index++)
            {
                var choice = index;
                _choiceButtons[index] = CreateButton($"Choice{index + 1}", LevelUpPanel.transform, new Vector2(0, -90 - index * 80), new Vector2(360, 64), out _choiceTexts[index]);
                _choiceButtons[index].onClick.AddListener(() => onSelect(choice));
            }

            // 결과 화면
            GameOverPanel = CreatePanel("GameOverPanel", root, new Vector2(420, 300));
            CreateTitle(GameOverPanel.transform, "GAME OVER");

            ResultText = CreateText("ResultText", GameOverPanel.transform, 26, TextAlignmentOptions.Center);
            var resultRect = ResultText.rectTransform;
            resultRect.anchorMin = resultRect.anchorMax = resultRect.pivot = new Vector2(0.5f, 0.5f);
            resultRect.sizeDelta = new Vector2(360, 120);

            var restart = CreateButton("RestartButton", GameOverPanel.transform, new Vector2(0, -220), new Vector2(200, 56), out var restartText);
            restartText.SetText("Restart");
            restart.onClick.AddListener(() => onRestart());

            LevelUpPanel.SetActive(false);
            GameOverPanel.SetActive(false);
        }

        public void SetHealth(float ratio)
        {
            _healthFill.anchorMax = new Vector2(Mathf.Clamp01(ratio), 1);
        }

        public void SetExperience(int level, float ratio)
        {
            _experienceFill.anchorMax = new Vector2(Mathf.Clamp01(ratio), 1);
            _levelText.SetText($"Lv {level}");
        }

        public void ShowLevelUp(IReadOnlyList<UpgradeDefinition> choices, PlayerStats stats)
        {
            for (var index = 0; index < _choiceButtons.Length; index++)
            {
                var visible = index < choices.Count;
                _choiceButtons[index].gameObject.SetActive(visible);

                if (!visible)
                    continue;

                var upgrade = choices[index];
                var nextLevel = stats.GetLevel(upgrade) + 1;
                _choiceTexts[index].SetText($"{index + 1}. {upgrade.displayName}  Lv {nextLevel}/{upgrade.maxLevel}\n<size=70%>{upgrade.Description}</size>");
            }

            LevelUpPanel.SetActive(true);
        }

        public void HideLevelUp()
        {
            LevelUpPanel.SetActive(false);
        }

        public void ShowGameOver(string result)
        {
            LevelUpPanel.SetActive(false);
            ResultText.SetText(result);
            GameOverPanel.SetActive(true);
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            return (RectTransform)gameObject.transform;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        private static Image CreateImage(string name, Transform parent, Color color)
        {
            var image = CreateRect(name, parent).gameObject.AddComponent<Image>();
            image.color = color;
            return image;
        }

        // 배경 안에 채움 영역을 두고, 채움 영역의 오른쪽 Anchor로 비율을 표시
        private static RectTransform CreateBar(string name, RectTransform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 size, Color fillColor)
        {
            var background = CreateImage(name, parent, BarBackgroundColor).rectTransform;
            background.anchorMin = anchorMin;
            background.anchorMax = anchorMax;
            background.pivot = new Vector2(0.5f, 0.5f);
            background.anchoredPosition = position;
            background.sizeDelta = size;

            var fill = CreateImage("Fill", background, fillColor).rectTransform;
            Stretch(fill);
            fill.anchorMax = new Vector2(0, 1);
            return fill;
        }

        private static TextMeshProUGUI CreateText(string name, Transform parent, float fontSize, TextAlignmentOptions alignment)
        {
            var text = CreateRect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }

        private static GameObject CreatePanel(string name, Transform parent, Vector2 size)
        {
            var panel = CreateImage(name, parent, PanelColor).rectTransform;
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = size;
            return panel.gameObject;
        }

        private static void CreateTitle(Transform panel, string title)
        {
            var text = CreateText("Title", panel, 36, TextAlignmentOptions.Center);
            var rect = text.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = new Vector2(0, -12);
            rect.sizeDelta = new Vector2(360, 48);
            text.SetText(title);
        }

        // position은 부모 오른쪽 위 기준. 체크 상자 + 라벨
        private static Toggle CreateToggle(string name, Transform parent, Vector2 position, string text, bool isOn)
        {
            var rect = CreateRect(name, parent);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 1);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(200, 32);

            var box = CreateImage("Box", rect, ButtonColor);
            var boxRect = box.rectTransform;
            boxRect.anchorMin = boxRect.anchorMax = boxRect.pivot = new Vector2(1, 0.5f);
            boxRect.anchoredPosition = Vector2.zero;
            boxRect.sizeDelta = new Vector2(28, 28);

            var check = CreateImage("Check", boxRect, CheckColor);
            var checkRect = check.rectTransform;
            Stretch(checkRect);
            checkRect.offsetMin = new Vector2(5, 5);
            checkRect.offsetMax = new Vector2(-5, -5);
            check.raycastTarget = false;

            var label = CreateText("Label", rect, 20, TextAlignmentOptions.Right);
            var labelRect = label.rectTransform;
            Stretch(labelRect);
            labelRect.offsetMax = new Vector2(-36, 0);
            label.SetText(text);

            var toggle = rect.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = box;
            toggle.graphic = check;
            toggle.SetIsOnWithoutNotify(isOn);
            return toggle;
        }

        // position은 패널 위쪽 가운데 기준
        private static Button CreateButton(string name, Transform parent, Vector2 position, Vector2 size, out TextMeshProUGUI label)
        {
            var image = CreateImage(name, parent, ButtonColor);
            var rect = image.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            label = CreateText("Label", rect, 22, TextAlignmentOptions.Center);
            Stretch(label.rectTransform);
            return button;
        }
    }
}
