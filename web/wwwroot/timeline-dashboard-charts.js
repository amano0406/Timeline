(() => {
  const charts = new Map();

  const labels = {
    events: "\u30a4\u30d9\u30f3\u30c8",
    items: "\u7d20\u6750",
    contextChars: "\u6982\u8981\u5316\u6e08\u307f\u672c\u6587",
    completed: "\u4f5c\u6210\u6e08\u307f",
    pending: "\u672a\u4f5c\u6210",
    failed: "\u5931\u6557",
    summaryCount: "\u6982\u8981\u4f5c\u6210\u6e08\u307f",
    countSuffix: "\u4ef6",
    oku: "\u5104",
    man: "\u4e07",
  };

  const palette = {
    audio: "#0f766e",
    video: "#2563eb",
    image: "#7c3aed",
    chatgpt: "#16a34a",
    "windows-codex": "#334155",
    pc: "#d97706",
    unknown: "#64748b",
  };

  function destroyChart(id) {
    const existing = charts.get(id);
    if (existing) {
      existing.destroy();
      charts.delete(id);
    }
  }

  function renderChart(id, config) {
    const canvas = document.getElementById(id);
    if (!canvas || !window.Chart) {
      return false;
    }

    destroyChart(id);
    charts.set(id, new window.Chart(canvas, config));
    return true;
  }

  function compact(value) {
    const number = Number(value || 0);
    const absolute = Math.abs(number);
    if (absolute >= 100000000) {
      return `${(number / 100000000).toFixed(1).replace(/\.0$/, "")}${labels.oku}`;
    }
    if (absolute >= 10000) {
      return `${(number / 10000).toFixed(1).replace(/\.0$/, "")}${labels.man}`;
    }
    return new Intl.NumberFormat("ja-JP").format(number);
  }

  function productLabel(productId, stats) {
    const product = (stats.productTotals || []).find((entry) => entry.productId === productId);
    return product?.displayName || productId;
  }

  function productColor(productId, index) {
    const fallback = ["#0f766e", "#2563eb", "#7c3aed", "#16a34a", "#d97706", "#64748b"];
    return palette[productId] || fallback[index % fallback.length];
  }

  function renderDailyItems(stats) {
    const days = stats.dailyItems || [];
    const productIds = Array.from(new Set(
      days.flatMap((day) => Object.keys(day.productCounts || {}))
    ));

    const datasets = productIds.map((productId, index) => ({
      label: productLabel(productId, stats),
      data: days.map((day) => Number((day.productCounts || {})[productId] || 0)),
      backgroundColor: productColor(productId, index),
      borderRadius: 4,
      borderSkipped: false,
      maxBarThickness: 32,
    }));

    return renderChart("timeline-daily-items-chart", {
      type: "bar",
      data: {
        labels: days.map((day) => day.label || day.date),
        datasets,
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        plugins: {
          legend: {
            position: "bottom",
            labels: { boxWidth: 10, boxHeight: 10, usePointStyle: true },
          },
          tooltip: {
            callbacks: {
              afterBody(items) {
                const index = items[0]?.dataIndex ?? 0;
                const day = days[index] || {};
                return [`${labels.events} ${compact(day.eventCount)} ${labels.countSuffix}`];
              },
            },
          },
        },
        scales: {
          x: {
            stacked: true,
            grid: { display: false },
          },
          y: {
            stacked: true,
            beginAtZero: true,
            ticks: { precision: 0 },
          },
        },
      },
    });
  }

  function renderCumulativeContext(stats) {
    const days = stats.dailyItems || [];
    return renderChart("timeline-cumulative-context-chart", {
      type: "line",
      data: {
        labels: days.map((day) => day.label || day.date),
        datasets: [
          {
            label: labels.contextChars,
            data: days.map((day) => Number(day.cumulativeContextChars || 0)),
            borderColor: "#0f766e",
            backgroundColor: "rgba(15, 118, 110, 0.12)",
            fill: true,
            tension: 0.25,
            pointRadius: 2,
            pointHoverRadius: 4,
          },
          {
            label: labels.events,
            data: days.map((day) => Number(day.cumulativeEvents || 0)),
            borderColor: "#334155",
            backgroundColor: "rgba(51, 65, 85, 0.08)",
            fill: false,
            tension: 0.2,
            pointRadius: 0,
            yAxisID: "events",
          },
        ],
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        interaction: { mode: "index", intersect: false },
        plugins: {
          legend: {
            position: "bottom",
            labels: { boxWidth: 10, boxHeight: 10, usePointStyle: true },
          },
          tooltip: {
            callbacks: {
              label(context) {
                return `${context.dataset.label}: ${compact(context.parsed.y)}`;
              },
            },
          },
        },
        scales: {
          x: { grid: { display: false } },
          y: {
            beginAtZero: true,
            ticks: {
              callback(value) {
                return compact(value);
              },
            },
          },
          events: {
            position: "right",
            beginAtZero: true,
            grid: { drawOnChartArea: false },
            ticks: {
              callback(value) {
                return compact(value);
              },
            },
          },
        },
      },
    });
  }

  function renderSummaryCompletion(stats) {
    const completed = Number(stats.summaryCompletedItems || 0);
    const failed = Number(stats.summaryFailedItems || 0);
    const pending = Math.max(0, Number(stats.summaryTargetItems || 0) - completed - failed);

    return renderChart("timeline-summary-completion-chart", {
      type: "doughnut",
      data: {
        labels: [labels.completed, labels.pending, labels.failed],
        datasets: [
          {
            data: [completed, pending, failed],
            backgroundColor: ["#0f766e", "#cbd5e1", "#dc2626"],
            borderColor: "#ffffff",
            borderWidth: 3,
          },
        ],
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        cutout: "68%",
        plugins: {
          legend: {
            position: "bottom",
            labels: { boxWidth: 10, boxHeight: 10, usePointStyle: true },
          },
          tooltip: {
            callbacks: {
              label(context) {
                return `${context.label}: ${compact(context.parsed)} ${labels.countSuffix}`;
              },
            },
          },
        },
      },
    });
  }

  function renderProductContext(stats) {
    const products = (stats.productTotals || [])
      .slice()
      .sort((a, b) => Number(b.contextChars || 0) - Number(a.contextChars || 0));

    if (products.length === 0) {
      return true;
    }

    return renderChart("timeline-product-context-chart", {
      type: "bar",
      data: {
        labels: products.map((product) => product.displayName || product.productId),
        datasets: [
          {
            label: labels.contextChars,
            data: products.map((product) => Number(product.contextChars || 0)),
            backgroundColor: products.map((product, index) => productColor(product.productId, index)),
            borderRadius: 5,
            borderSkipped: false,
            maxBarThickness: 26,
          },
        ],
      },
      options: {
        indexAxis: "y",
        responsive: true,
        maintainAspectRatio: false,
        plugins: {
          legend: { display: false },
          tooltip: {
            callbacks: {
              label(context) {
                const product = products[context.dataIndex] || {};
                return [
                  `${labels.contextChars}: ${compact(product.contextChars)}`,
                  `${labels.items}: ${compact(product.itemCount)} ${labels.countSuffix}`,
                  `${labels.events}: ${compact(product.eventCount)} ${labels.countSuffix}`,
                  `${labels.summaryCount}: ${compact(product.summaryCount)} ${labels.countSuffix}`,
                ];
              },
            },
          },
        },
        scales: {
          x: {
            beginAtZero: true,
            ticks: {
              callback(value) {
                return compact(value);
              },
            },
          },
          y: {
            grid: { display: false },
          },
        },
      },
    });
  }

  window.timelineDashboardCharts = {
    render(stats) {
      if (!stats || !stats.available) {
        [
          "timeline-daily-items-chart",
          "timeline-cumulative-context-chart",
          "timeline-summary-completion-chart",
          "timeline-product-context-chart",
        ].forEach(destroyChart);
        return { ok: true, renderedCount: 0, message: "No dashboard stats." };
      }

      if (!window.Chart) {
        return { ok: false, renderedCount: 0, message: "Chart.js is not loaded." };
      }

      const renderResults = [
        renderDailyItems(stats),
        renderCumulativeContext(stats),
        renderSummaryCompletion(stats),
      ];
      if (document.getElementById("timeline-product-context-chart")) {
        renderResults.push(renderProductContext(stats));
      }

      const renderedCount = renderResults.filter(Boolean).length;
      const expectedCount = renderResults.length;

      return {
        ok: renderedCount === expectedCount,
        renderedCount,
        message: renderedCount === expectedCount
          ? ""
          : "Some dashboard chart canvases were not found.",
      };
    },
  };
})();
