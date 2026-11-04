#nullable enable
using System;
using System.Collections.Generic;
using AdaptiveSync.Demo.Mvvm;
using AdaptiveSync.Demo.UI;
using UnityEngine.UIElements;

namespace AdaptiveSync.Demo.Views
{
    public static class Bind
    {
        public static IDisposable Text(Label label, IReadOnlyObservable<string> source) =>
            source.Subscribe(value => label.text = value);

        public static IDisposable Text(Button button, IReadOnlyObservable<string> source) =>
            source.Subscribe(value => button.text = value);

        public static IDisposable Class(VisualElement element, string className, IReadOnlyObservable<bool> source) =>
            source.Subscribe(value => element.EnableInClassList(className, value));

        public static IDisposable Visible(VisualElement element, IReadOnlyObservable<bool> source) =>
            source.Subscribe(value => element.style.display = value ? DisplayStyle.Flex : DisplayStyle.None);

        public static IDisposable Enabled(VisualElement element, IReadOnlyObservable<bool> source) =>
            source.Subscribe(element.SetEnabled);

        public static IDisposable Command(Button button, ICommand command)
        {
            Action click = command.Execute;
            button.clicked += click;
            var enabled = command.CanExecute.Subscribe(button.SetEnabled);
            return new Subscription(() =>
            {
                button.clicked -= click;
                enabled.Dispose();
            });
        }

        public static IDisposable Tap(VisualElement element, ICommand command)
        {
            UiKit.OnTap(element, command.Execute);
            return new Subscription(() => { });
        }

        public static IDisposable TwoWay(TextField field, Observable<string> source)
        {
            var toView = source.Subscribe(value =>
            {
                if (field.value != value) field.SetValueWithoutNotify(value);
            });
            EventCallback<ChangeEvent<string>> toModel = e => source.Value = e.newValue;
            field.RegisterValueChangedCallback(toModel);
            return new Subscription(() =>
            {
                toView.Dispose();
                field.UnregisterValueChangedCallback(toModel);
            });
        }

        public static IDisposable Items<T>(VisualElement container, ObservableList<T> source, Func<T, VisualElement> template) =>
            source.Subscribe(items => Render(container, items, template));

        private static void Render<T>(VisualElement container, IReadOnlyList<T> items, Func<T, VisualElement> template)
        {
            container.Clear();
            foreach (var item in items)
                container.Add(template(item));
        }
    }
}
