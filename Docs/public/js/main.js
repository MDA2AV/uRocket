function computeMenuTranslation(switcher, optionsElement) {
  // Calculate the position of a language options element.
  const switcherRect = switcher.getBoundingClientRect();

  // Must be called before optionsElement.clientWidth.
  optionsElement.style.minWidth = `${Math.max(switcherRect.width, 50)}px`;

  const isOnTop = switcher.dataset.location === 'top';
  const isOnBottom = switcher.dataset.location === 'bottom';
  const isOnBottomRight = switcher.dataset.location === 'bottom-right';
  const isRTL = document.documentElement.dir === 'rtl'

  // Stuck on the left side of the switcher.
  let x = switcherRect.left;

  if (isOnTop && !isRTL || isOnBottom && isRTL || isOnBottomRight && !isRTL) {
    // Stuck on the right side of the switcher.
    x = switcherRect.right - optionsElement.clientWidth;
  }

  // Stuck on the top of the switcher.
  let y = switcherRect.top - window.innerHeight - 10;

  if (isOnTop) {
    // Stuck on the bottom of the switcher.
    y = switcherRect.top - window.innerHeight + optionsElement.clientHeight + switcher.clientHeight + 4;
  }

  return { x: x, y: y };
}

function toggleMenu(switcher) {
  const optionsElement = switcher.nextElementSibling;

  optionsElement.classList.toggle('hx:hidden');

  // Calculate the position of a language options element.
  const translate = computeMenuTranslation(switcher, optionsElement);

  optionsElement.style.transform = `translate3d(${translate.x}px, ${translate.y}px, 0)`;
}

function resizeMenu(switcher) {
  const optionsElement = switcher.nextElementSibling;

  if (optionsElement.classList.contains('hx:hidden')) return;

  // Calculate the position of a language options element.
  const translate = computeMenuTranslation(switcher, optionsElement);

  optionsElement.style.transform = `translate3d(${translate.x}px, ${translate.y}px, 0)`;
}

;
// Light / Dark theme toggle
(function () {
  const defaultTheme = 'dark'
  const themes = ["light", "dark"];

  const themeToggleButtons = document.querySelectorAll(".hextra-theme-toggle");
  const themeToggleOptions = document.querySelectorAll(".hextra-theme-toggle-options p");

  function applyTheme(theme) {
    theme = themes.includes(theme) ? theme : "system";

    themeToggleButtons.forEach((btn) => btn.parentElement.dataset.theme = theme );

    localStorage.setItem("color-theme", theme);
  }

  function switchTheme(theme) {
    setTheme(theme);
    applyTheme(theme);
  }

  const colorTheme = "color-theme" in localStorage ? localStorage.getItem("color-theme") : defaultTheme;
  switchTheme(colorTheme);

  // Add click event handler to the menu items.
  themeToggleOptions.forEach((option) => {
    option.addEventListener("click", function (e) {
      e.preventDefault();

      switchTheme(option.dataset.item);
    })
  })

  // Add click event handler to the buttons
  themeToggleButtons.forEach((toggler) => {
    toggler.addEventListener("click", function (e) {
      e.preventDefault();

      toggleMenu(toggler);
    });
  });

  window.addEventListener("resize", () => themeToggleButtons.forEach(resizeMenu))

  // Dismiss the menu when clicking outside
  document.addEventListener('click', (e) => {
    if (e.target.closest('.hextra-theme-toggle') === null) {
      themeToggleButtons.forEach((toggler) => {
        toggler.dataset.state = 'closed';
        toggler.nextElementSibling.classList.add('hx:hidden');
      });
    }
  });

  // Listen for system theme changes
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
    if (localStorage.getItem("color-theme") === "system") {
      setTheme("system");
    }
  });
})();

;
//
;
// Hamburger menu for mobile navigation

document.addEventListener('DOMContentLoaded', function () {
  const menu = document.querySelector('.hextra-hamburger-menu');
  const sidebarContainer = document.querySelector('.hextra-sidebar-container');

  function toggleMenu() {
    // Toggle the hamburger menu
    menu.querySelector('svg').classList.toggle('open');

    // When the menu is open, we want to show the navigation sidebar
    sidebarContainer.classList.toggle('hx:max-md:[transform:translate3d(0,-100%,0)]');
    sidebarContainer.classList.toggle('hx:max-md:[transform:translate3d(0,0,0)]');

    // When the menu is open, we want to prevent the body from scrolling
    document.body.classList.toggle('hx:overflow-hidden');
    document.body.classList.toggle('hx:md:overflow-auto');
  }

  menu.addEventListener('click', (e) => {
    e.preventDefault();
    toggleMenu();
  });

  // Select all anchor tags in the sidebar container
  const sidebarLinks = sidebarContainer.querySelectorAll('a');

  // Add click event listener to each anchor tag
  sidebarLinks.forEach(link => {
    link.addEventListener('click', (e) => {
      // Check if the href attribute contains a hash symbol (links to a heading)
      if (link.getAttribute('href') && link.getAttribute('href').startsWith('#')) {
        // Only dismiss overlay on mobile view
        if (window.innerWidth < 768) {
          toggleMenu();
        }
      }
    });
  });
});

;
// Copy button for code blocks

document.addEventListener('DOMContentLoaded', function () {
  const getCopyIcon = () => {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.innerHTML = `
      <path stroke-linecap="round" stroke-linejoin="round" d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
    `;
    svg.setAttribute('fill', 'none');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('stroke', 'currentColor');
    svg.setAttribute('stroke-width', '2');
    return svg;
  }

  const getSuccessIcon = () => {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.innerHTML = `
      <path stroke-linecap="round" stroke-linejoin="round" d="M5 13l4 4L19 7" />
    `;
    svg.setAttribute('fill', 'none');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('stroke', 'currentColor');
    svg.setAttribute('stroke-width', '2');
    return svg;
  }

  document.querySelectorAll('.hextra-code-copy-btn').forEach(function (button) {
    // Add copy and success icons
    button.querySelector('.hextra-copy-icon')?.appendChild(getCopyIcon());
    button.querySelector('.hextra-success-icon')?.appendChild(getSuccessIcon());

    // Add click event listener for copy button
    button.addEventListener('click', function (e) {
      e.preventDefault();
      // Get the code target
      const target = button.parentElement.previousElementSibling;
      let codeElement;
      if (target.tagName === 'CODE') {
        codeElement = target;
      } else {
        // Select the last code element in case line numbers are present
        const codeElements = target.querySelectorAll('code');
        codeElement = codeElements[codeElements.length - 1];
      }
      if (codeElement) {
        let code = codeElement.innerText;
        // Replace double newlines with single newlines in the innerText
        // as each line inside <span> has trailing newline '\n'
        if ("lang" in codeElement.dataset) {
          code = code.replace(/\n\n/g, '\n');
        }
        navigator.clipboard.writeText(code).then(function () {
          button.classList.add('copied');
          setTimeout(function () {
            button.classList.remove('copied');
          }, 1000);
        }).catch(function (err) {
          console.error('Failed to copy text: ', err);
        });
      } else {
        console.error('Target element not found');
      }
    });
  });
});

;
(function () {
  function updateGroup(container, index) {
    const tabs = Array.from(container.querySelectorAll('.hextra-tabs-toggle'));
    tabs.forEach((tab, i) => {
      tab.dataset.state = i === index ? 'selected' : '';
      if (i === index) {
        tab.setAttribute('aria-selected', 'true');
        tab.tabIndex = 0;
      } else {
        tab.removeAttribute('aria-selected');
        tab.removeAttribute('tabindex');
      }
    });
    const panelsContainer = container.parentElement.nextElementSibling;
    if (!panelsContainer) return;
    Array.from(panelsContainer.children).forEach((panel, i) => {
      panel.dataset.state = i === index ? 'selected' : '';
      if (i === index) {
        panel.tabIndex = 0;
      } else {
        panel.removeAttribute('tabindex');
      }
    });
  }

  const syncGroups = document.querySelectorAll('[data-tab-group]');

  syncGroups.forEach((group) => {
    const key = encodeURIComponent(group.dataset.tabGroup);
    const saved = localStorage.getItem('hextra-tab-' + key);
    if (saved !== null) {
      updateGroup(group, parseInt(saved, 10));
    }
  });

  document.querySelectorAll('.hextra-tabs-toggle').forEach((button) => {
    button.addEventListener('click', function (e) {
      const container = e.target.parentElement;
      const index = Array.from(container.querySelectorAll('.hextra-tabs-toggle')).indexOf(
        e.target
      );
      
      if (container.dataset.tabGroup) {
        // Sync behavior: update all tab groups with the same name
        const tabGroupValue = container.dataset.tabGroup;
        const key = encodeURIComponent(tabGroupValue);
        document
          .querySelectorAll('[data-tab-group="' + tabGroupValue + '"]')
          .forEach((grp) => updateGroup(grp, index));
        localStorage.setItem('hextra-tab-' + key, index.toString());
      } else {
        // Non-sync behavior: update only this specific tab group
        updateGroup(container, index);
      }
    });
  });
})();

;
(function () {
  const languageSwitchers = document.querySelectorAll('.hextra-language-switcher');

  languageSwitchers.forEach((switcher) => {
    switcher.addEventListener('click', (e) => {
      e.preventDefault();

      switcher.dataset.state = switcher.dataset.state === 'open' ? 'closed' : 'open';

      toggleMenu(switcher);
    });
  });

  window.addEventListener("resize", () => languageSwitchers.forEach(resizeMenu))

  // Dismiss language switcher when clicking outside
  document.addEventListener('click', (e) => {
    if (e.target.closest('.hextra-language-switcher') === null) {
      languageSwitchers.forEach((switcher) => {
        switcher.dataset.state = 'closed';
        const optionsElement = switcher.nextElementSibling;
        optionsElement.classList.add('hx:hidden');
      });
    }
  });
})();

;
(function () {
  const hiddenClass = "hx:hidden";
  const dropdownToggles = document.querySelectorAll(".hextra-nav-menu-toggle");

  dropdownToggles.forEach((toggle) => {
    toggle.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();

      // Close all other dropdowns first
      dropdownToggles.forEach((otherToggle) => {
        if (otherToggle !== toggle) {
          otherToggle.dataset.state = "closed";
          const otherMenuItems = otherToggle.nextElementSibling;
          otherMenuItems.classList.add(hiddenClass);
        }
      });

      // Toggle current dropdown
      const isOpen = toggle.dataset.state === "open";
      toggle.dataset.state = isOpen ? "closed" : "open";
      const menuItemsElement = toggle.nextElementSibling;

      if (!isOpen) {
        // Position dropdown centered with toggle
        menuItemsElement.style.position = "absolute";
        menuItemsElement.style.top = "100%";
        menuItemsElement.style.left = "50%";
        menuItemsElement.style.transform = "translateX(-50%)";
        menuItemsElement.style.zIndex = "1000";

        // Show dropdown
        menuItemsElement.classList.remove(hiddenClass);
      } else {
        // Hide dropdown
        menuItemsElement.classList.add(hiddenClass);
      }
    });
  });

  // Dismiss dropdown when clicking outside
  document.addEventListener("click", (e) => {
    if (e.target.closest(".hextra-nav-menu-toggle") === null) {
      dropdownToggles.forEach((toggle) => {
        toggle.dataset.state = "closed";
        const menuItemsElement = toggle.nextElementSibling;
        menuItemsElement.classList.add(hiddenClass);
      });
    }
  });

  // Close dropdowns on escape key
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") {
      dropdownToggles.forEach((toggle) => {
        toggle.dataset.state = "closed";
        const menuItemsElement = toggle.nextElementSibling;
        menuItemsElement.classList.add(hiddenClass);
      });
    }
  });
})();

;
// Script for filetree shortcode collapsing/expanding folders used in the theme
// ======================================================================
document.addEventListener("DOMContentLoaded", function () {
  const folders = document.querySelectorAll(".hextra-filetree-folder");
  folders.forEach(function (folder) {
    folder.addEventListener("click", function () {
      Array.from(folder.children).forEach(function (el) {
        el.dataset.state = el.dataset.state === "open" ? "closed" : "open";
      });
      folder.nextElementSibling.dataset.state = folder.nextElementSibling.dataset.state === "open" ? "closed" : "open";
    });
  });
});

;
document.addEventListener("DOMContentLoaded", function () {
  scrollToActiveItem();
  enableCollapsibles();
});

function enableCollapsibles() {
  const buttons = document.querySelectorAll(".hextra-sidebar-collapsible-button");
  buttons.forEach(function (button) {
    button.addEventListener("click", function (e) {
      e.preventDefault();
      const list = button.parentElement.parentElement;
      if (list) {
        list.classList.toggle("open")
      }
    });
  });
}

function scrollToActiveItem() {
  const sidebarScrollbar = document.querySelector("aside.hextra-sidebar-container > .hextra-scrollbar");
  const activeItems = document.querySelectorAll(".hextra-sidebar-active-item");
  const visibleActiveItem = Array.from(activeItems).find(function (activeItem) {
    return activeItem.getBoundingClientRect().height > 0;
  });

  if (!visibleActiveItem) {
    return;
  }

  const yOffset = visibleActiveItem.clientHeight;
  const yDistance = visibleActiveItem.getBoundingClientRect().top - sidebarScrollbar.getBoundingClientRect().top;
  sidebarScrollbar.scrollTo({
    behavior: "instant",
    top: yDistance - yOffset
  });
}

;
// Back to top button

document.addEventListener("DOMContentLoaded", function () {
  const backToTop = document.querySelector("#backToTop");
  if (backToTop) {
    document.addEventListener("scroll", (e) => {
      if (window.scrollY > 300) {
        backToTop.classList.remove("hx:opacity-0");
      } else {
        backToTop.classList.add("hx:opacity-0");
      }
    });
  }
});

function scrollUp() {
  window.scroll({
    top: 0,
    left: 0,
    behavior: "smooth",
  });
}

;
/**
 * TOC Scroll - Highlights active TOC links based on visible headings
 * 
 * Uses Intersection Observer to track heading visibility and applies
 * 'hextra-toc-active' class to corresponding TOC links. Selects the
 * topmost heading when multiple are visible.
 * 
 * Requires: .hextra-toc element, matching heading IDs, toc.css styles
 */
document.addEventListener("DOMContentLoaded", function () {
  const toc = document.querySelector(".hextra-toc");
  if (!toc) return;

  const tocLinks = toc.querySelectorAll('a[href^="#"]');
  if (tocLinks.length === 0) return;

  const headingIds = Array.from(tocLinks).map((link) => link.getAttribute("href").substring(1));

  const headings = headingIds.map((id) => document.getElementById(decodeURIComponent(id))).filter(Boolean);
  if (headings.length === 0) return;

  let currentActiveLink = null;
  let isHashNavigation = false;

  // Create intersection observer
  const observer = new IntersectionObserver(
    (entries) => {
      // Skip observer updates during hash navigation
      if (isHashNavigation) return;

      const visibleHeadings = entries.filter((entry) => entry.isIntersecting).map((entry) => entry.target);

      if (visibleHeadings.length === 0) return;

      // Find the heading closest to the top of the viewport
      const topMostHeading = visibleHeadings.reduce((closest, heading) => {
        const headingTop = heading.getBoundingClientRect().top;
        const closestTop = closest.getBoundingClientRect().top;
        return Math.abs(headingTop) < Math.abs(closestTop) ? heading : closest;
      });

      // Encode the id and make it lowercase to match the TOC link
      const targetId = encodeURIComponent(topMostHeading.id).toLowerCase();
      const targetLink = toc.querySelector(`a[href="#${targetId}"]`);

      if (targetLink && targetLink !== currentActiveLink) {
        // Remove active class from previous link
        if (currentActiveLink) {
          currentActiveLink.classList.remove("hextra-toc-active");
        }

        // Add active class to current link
        targetLink.classList.add("hextra-toc-active");
        currentActiveLink = targetLink;
      }
    },
    {
      rootMargin: "-20px 0px -60% 0px", // Adjust sensitivity
      threshold: [0, 0.1, 0.5, 1],
    }
  );

  // Observe all headings
  headings.forEach((heading) => observer.observe(heading));

  // Handle direct navigation to page with hash
  function handleHashNavigation() {
    const hash = window.location.hash; // already url encoded
    if (hash) {
      const targetLink = toc.querySelector(`a[href="${hash}"]`);
      if (targetLink) {
        // Disable observer temporarily during hash navigation
        isHashNavigation = true;

        if (currentActiveLink) {
          currentActiveLink.classList.remove("hextra-toc-active");
        }
        targetLink.classList.add("hextra-toc-active");
        currentActiveLink = targetLink;

        // Re-enable observer after scroll settles
        setTimeout(() => { isHashNavigation = false; }, 500);
        return;
      }
    }
  }

  // Handle hash changes navigation
  window.addEventListener("hashchange", handleHashNavigation);

  // Handle initial load
  setTimeout(handleHashNavigation, 100);
});

;
// 
(function () {
  const faviconEl = document.getElementById("favicon-svg");
  const faviconDarkExists = "false" === "true";

  if (faviconEl && faviconDarkExists) {
    const lightFavicon = '/favicon.svg';
    const darkFavicon = '/favicon-dark.svg';

    const darkModeQuery = window.matchMedia("(prefers-color-scheme: dark)");

    function updateFavicon(e) {
      faviconEl.href = e.matches ? darkFavicon : lightFavicon;
    }

    // Set favicon on load
    updateFavicon(darkModeQuery);

    // Listen for system preference changes
    darkModeQuery.addEventListener("change", updateFavicon);
  }
})();
