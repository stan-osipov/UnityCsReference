// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityEditor.PackageManager.UI
{
    internal class PackageList : VisualElement
    {
        internal new class UxmlFactory : UxmlFactory<PackageList> {}

        private Dictionary<string, PackageItem> m_PackageItemsLookup;

        private ResourceLoader m_ResourceLoader;
        private UnityConnectProxy m_UnityConnect;
        private PackageFiltering m_PackageFiltering;
        private PackageManagerPrefs m_PackageManagerPrefs;
        private PackageDatabase m_PackageDatabase;
        private PageManager m_PageManager;
        private void ResolveDependencies()
        {
            var container = ServicesContainer.instance;
            m_ResourceLoader = container.Resolve<ResourceLoader>();
            m_UnityConnect = container.Resolve<UnityConnectProxy>();
            m_PackageFiltering = container.Resolve<PackageFiltering>();
            m_PackageManagerPrefs = container.Resolve<PackageManagerPrefs>();
            m_PackageDatabase = container.Resolve<PackageDatabase>();
            m_PageManager = container.Resolve<PageManager>();
        }

        internal IEnumerable<PackageItem> packageItems
        {
            get { return itemsList.Children().Cast<PackageItem>(); }
        }

        public PackageList()
        {
            ResolveDependencies();

            var root = m_ResourceLoader.GetTemplate("PackageList.uxml");
            Add(root);
            root.StretchToParentSize();
            cache = new VisualElementCache(root);

            viewDataKey = "package-list-key";
            scrollView.viewDataKey = "package-list-scrollview-key";

            HidePackagesShowMessage(false, false);

            loginButton.clickable.clicked += OnLoginClicked;

            RegisterCallback<AttachToPanelEvent>(OnEnterPanel);
            RegisterCallback<DetachFromPanelEvent>(OnLeavePanel);

            RegisterCallback<GeometryChangedEvent>(OnGeometryChange);

            m_PackageItemsLookup = new Dictionary<string, PackageItem>();

            focusable = true;
        }

        public void OnEnable()
        {
            m_PackageDatabase.onPackageProgressUpdate += OnPackageProgressUpdate;

            m_PageManager.onRefreshOperationStart += OnRefreshOperationStart;
            m_PageManager.onRefreshOperationFinish += OnRefreshOperationFinish;

            m_PageManager.onVisualStateChange += OnVisualStateChange;
            m_PageManager.onListRebuild += OnListRebuild;
            m_PageManager.onListUpdate += OnListUpdate;

            m_UnityConnect.onUserLoginStateChange += OnUserLoginStateChange;

            // manually build the items on initialization to refresh the UI
            OnListRebuild(m_PageManager.GetCurrentPage());
        }

        public void OnDisable()
        {
            m_PackageDatabase.onPackageProgressUpdate -= OnPackageProgressUpdate;

            m_PageManager.onRefreshOperationStart -= OnRefreshOperationStart;
            m_PageManager.onRefreshOperationFinish -= OnRefreshOperationFinish;

            m_PageManager.onVisualStateChange -= OnVisualStateChange;
            m_PageManager.onListRebuild -= OnListRebuild;
            m_PageManager.onListUpdate -= OnListUpdate;

            m_UnityConnect.onUserLoginStateChange -= OnUserLoginStateChange;
        }

        private PackageItem GetPackageItem(IPackage package)
        {
            return GetPackageItem(package?.uniqueId);
        }

        private PackageItem GetPackageItem(string packageUniqueId)
        {
            return string.IsNullOrEmpty(packageUniqueId) ? null : m_PackageItemsLookup.Get(packageUniqueId);
        }

        private ISelectableItem GetSelectedItem()
        {
            var selectedVersion = m_PageManager.GetSelectedVersion();
            var packageItem = GetPackageItem(selectedVersion?.packageUniqueId);
            if (packageItem == null)
                return null;

            if (packageItem.targetVersion == selectedVersion)
                return packageItem;

            return packageItem.versionItems.FirstOrDefault(v => v.targetVersion == selectedVersion);
        }

        private void OnUserLoginStateChange(bool loggedIn)
        {
            if (m_PackageFiltering.currentFilterTab == PackageFilterTab.AssetStore)
                RefreshList(false);
        }

        private void ShowPackages(bool updateScrollPosition)
        {
            UIUtils.SetElementDisplay(scrollView, true);
            UIUtils.SetElementDisplay(emptyArea, false);

            var page = m_PageManager.GetCurrentPage();
            var selectedVersion = page.GetSelectedVersion();
            var selectedVisualState = selectedVersion != null ? page.GetVisualState(selectedVersion.packageUniqueId) : null;
            if (selectedVisualState?.visible != true)
            {
                var firstVisible = page.visualStates.FirstOrDefault(v => v.visible);
                if (firstVisible != null)
                {
                    IPackage package;
                    IPackageVersion version;
                    m_PackageDatabase.GetPackageAndVersion(firstVisible.packageUniqueId, firstVisible.selectedVersionId, out package, out version);
                    m_PageManager.SetSelected(package, version);
                }
                else
                    m_PageManager.ClearSelection();
            }

            if (updateScrollPosition)
                ScrollIfNeeded();
        }

        private void HidePackagesShowLogin()
        {
            UIUtils.SetElementDisplay(scrollView, false);
            UIUtils.SetElementDisplay(emptyArea, true);
            UIUtils.SetElementDisplay(noPackagesLabel, false);
            UIUtils.SetElementDisplay(loginContainer, true);

            m_PageManager.ClearSelection();
        }

        private void HidePackagesShowMessage(bool isRefreshInProgress, bool isInitialFetchingDone)
        {
            UIUtils.SetElementDisplay(scrollView, false);
            UIUtils.SetElementDisplay(emptyArea, true);
            UIUtils.SetElementDisplay(noPackagesLabel, true);
            UIUtils.SetElementDisplay(loginContainer, false);

            if (isRefreshInProgress)
            {
                if (!isInitialFetchingDone)
                    noPackagesLabel.text = L10n.Tr("Fetching packages...");
                else
                    noPackagesLabel.text = L10n.Tr("Refreshing packages...");
            }
            else if (string.IsNullOrEmpty(m_PackageFiltering.currentSearchText))
            {
                if (!isInitialFetchingDone)
                    noPackagesLabel.text = string.Empty;
                else
                    noPackagesLabel.text = L10n.Tr("There are no packages.");
            }
            else
            {
                const int maxSearchTextToDisplay = 64;
                var searchText = m_PackageFiltering.currentSearchText;
                if (searchText?.Length > maxSearchTextToDisplay)
                    searchText = searchText.Substring(0, maxSearchTextToDisplay) + "...";
                noPackagesLabel.text = string.Format(L10n.Tr("No results for \"{0}\""), searchText);
            }

            m_PageManager.ClearSelection();
        }

        private void RefreshList(bool updateScrollPosition)
        {
            var isAssetStore = m_PackageFiltering.currentFilterTab == PackageFilterTab.AssetStore;
            var isLoggedIn = m_UnityConnect.isUserLoggedIn;

            if (isAssetStore && !isLoggedIn)
            {
                HidePackagesShowLogin();
                return;
            }

            var page = m_PageManager.GetCurrentPage();
            var isListEmpty = !page.visualStates.Any(v => v.visible);
            var isInitialFetchingDone = m_PageManager.IsInitialFetchingDone();
            if (isListEmpty || !isInitialFetchingDone)
            {
                HidePackagesShowMessage(m_PageManager.IsRefreshInProgress(), isInitialFetchingDone);
                return;
            }

            ShowPackages(updateScrollPosition);
        }

        private void OnLoginClicked()
        {
            m_UnityConnect.ShowLogin();
        }

        private void OnEnterPanel(AttachToPanelEvent e)
        {
            if (panel != null)
                panel.visualTree.RegisterCallback<KeyDownEvent>(OnKeyDownShortcut);
        }

        private void OnLeavePanel(DetachFromPanelEvent e)
        {
            if (panel != null)
                panel.visualTree.UnregisterCallback<KeyDownEvent>(OnKeyDownShortcut);
        }

        private void OnGeometryChange(GeometryChangedEvent evt)
        {
            float containerHeight = resolvedStyle.height;
            if (!float.IsNaN(containerHeight))
                m_PackageManagerPrefs.numItemsPerPage = (int)(containerHeight / PackageItem.k_ElementHeight);
        }

        private void OnPackageProgressUpdate(IPackage package)
        {
            GetPackageItem(package)?.UpdateStatusIcon();
        }

        private void OnRefreshOperationStart()
        {
            RefreshList(false);
        }

        private void OnRefreshOperationFinish()
        {
            RefreshList(false);
        }

        internal void OnFocus()
        {
            ScrollIfNeeded();
        }

        private void ScrollIfNeeded(ScrollView container = null, VisualElement target = null)
        {
            container = container ?? scrollView;
            target = target ?? GetSelectedItem()?.element;

            if (container == null || target == null)
                return;

            if (float.IsNaN(target.layout.height))
            {
                EditorApplication.delayCall += () => ScrollIfNeeded(container, target);
                return;
            }

            var scrollViews = UIUtils.GetParentsOfType<ScrollView>(target);
            foreach (var scrollview in scrollViews)
                UIUtils.ScrollIfNeeded(scrollview, target);
        }

        private void SetSelectedItemExpanded(bool value)
        {
            var selectedVersion = m_PageManager.GetSelectedVersion();
            GetPackageItem(selectedVersion?.packageUniqueId)?.SetExpanded(value);
        }

        private void OnKeyDownShortcut(KeyDownEvent evt)
        {
            if (!UIUtils.IsElementVisible(scrollView))
                return;

            if (evt.keyCode == KeyCode.RightArrow)
            {
                SetSelectedItemExpanded(true);
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.LeftArrow)
            {
                SetSelectedItemExpanded(false);
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.UpArrow)
            {
                if (SelectBy(-1))
                    evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.DownArrow)
            {
                if (SelectBy(1))
                    evt.StopPropagation();
            }
        }

        private PackageItem AddOrUpdatePackageItem(IPackage package)
        {
            if (package == null)
                return null;

            var item = GetPackageItem(package);
            if (item != null)
                item.SetPackage(package);
            else
            {
                item = new PackageItem(package);
                itemsList.Add(item);
                m_PackageItemsLookup[package.uniqueId] = item;
            }
            return item;
        }

        private PackageItem RemovePackageItem(IPackage package)
        {
            var item = GetPackageItem(package);
            if (item != null)
            {
                itemsList.Remove(item);
                m_PackageItemsLookup.Remove(package.uniqueId);
            }
            return item;
        }

        private void OnVisualStateChange(IEnumerable<VisualState> visualStates)
        {
            if (!visualStates.Any())
                return;

            foreach (var state in visualStates)
                GetPackageItem(state.packageUniqueId)?.UpdateVisualState(state);

            RefreshList(true);
        }

        private void OnListRebuild(IPage page)
        {
            itemsList.Clear();
            m_PackageItemsLookup.Clear();

            foreach (var visualState in page.visualStates)
            {
                var package = m_PackageDatabase.GetPackage(visualState.packageUniqueId);
                var packageItem = AddOrUpdatePackageItem(package);
                packageItem?.UpdateVisualState(visualState);
            }

            RefreshList(true);
        }

        private void OnListUpdate(IPage page, IEnumerable<IPackage> addedOrUpated, IEnumerable<IPackage> removed, bool reorder)
        {
            addedOrUpated = addedOrUpated ?? Enumerable.Empty<IPackage>();
            removed = removed ?? Enumerable.Empty<IPackage>();

            var numItems = m_PackageItemsLookup.Count;
            foreach (var package in removed)
                RemovePackageItem(package);

            var itemsRemoved = numItems != m_PackageItemsLookup.Count;
            numItems = m_PackageItemsLookup.Count;

            foreach (var package in addedOrUpated)
            {
                var packageItem = AddOrUpdatePackageItem(package);
                var visualState = page.GetVisualState(package.uniqueId);
                packageItem.UpdateVisualState(visualState);
            }
            var itemsAdded = numItems != m_PackageItemsLookup.Count;

            if (reorder)
            {
                // re-order if there are any added or updated items
                itemsList.Clear();
                foreach (var state in page.visualStates)
                    itemsList.Add(GetPackageItem(state.packageUniqueId));
                m_PackageItemsLookup = packageItems.ToDictionary(item => item.package.uniqueId, item => item);
            }

            if (itemsRemoved || itemsAdded)
                RefreshList(true);
        }

        public List<ISelectableItem> GetSelectableItems()
        {
            return packageItems.SelectMany(item => item.GetSelectableItems()).ToList();
        }

        internal bool SelectBy(int delta)
        {
            var list = GetSelectableItems();
            var selection = GetSelectedItem();
            if (selection != null)
            {
                var index = list.IndexOf(selection);

                var direction = Math.Sign(delta);
                delta = Math.Abs(delta);
                var nextIndex = index;
                var numVisibleElement = 0;
                ISelectableItem nextElement = null;
                while (numVisibleElement < delta)
                {
                    nextIndex += direction;
                    if (nextIndex >= list.Count)
                        return false;
                    if (nextIndex < 0)
                        return false;
                    nextElement = list.ElementAt(nextIndex);
                    if (UIUtils.IsElementVisible(nextElement.element))
                        ++numVisibleElement;
                }

                m_PageManager.SetSelected(nextElement.package, nextElement.targetVersion, true);

                foreach (var scrollView in UIUtils.GetParentsOfType<ScrollView>(nextElement.element))
                    ScrollIfNeeded(scrollView, nextElement.element);
            }

            return true;
        }

        private VisualElementCache cache { get; set; }

        private ScrollView scrollView { get { return cache.Get<ScrollView>("scrollView"); } }
        private VisualElement itemsList { get { return cache.Get<VisualElement>("itemsList"); } }
        private VisualElement emptyArea { get { return cache.Get<VisualElement>("emptyArea"); } }
        private Label noPackagesLabel { get { return cache.Get<Label>("noPackagesLabel"); } }
        private VisualElement loginContainer { get { return cache.Get<VisualElement>("loginContainer"); } }
        private Button loginButton { get { return cache.Get<Button>("loginButton"); } }
    }
}
