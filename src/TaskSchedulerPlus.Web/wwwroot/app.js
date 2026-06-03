// Browser helpers called from Blazor.
window.jobScheduler = window.jobScheduler || {};

window.jobScheduler.downloadTextFile = (fileName, content, contentType) => {
    const blob = new Blob([content], { type: contentType || "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");

    link.href = url;
    link.download = fileName;
    link.style.display = "none";
    document.body.appendChild(link);
    link.click();
    link.remove();

    URL.revokeObjectURL(url);
};

window.jobScheduler.downloadFileFromStream = async (fileName, contentStreamReference, contentType) => {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer], { type: contentType || "application/octet-stream" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");

    link.href = url;
    link.download = fileName;
    link.style.display = "none";
    document.body.appendChild(link);
    link.click();
    link.remove();

    URL.revokeObjectURL(url);
};

window.jobScheduler.refreshTooltips = () => {
    if (!window.bootstrap?.Tooltip) {
        return;
    }

    document.querySelectorAll("[data-job-tooltip='true']").forEach((element) => {
        const instance = bootstrap.Tooltip.getInstance(element);
        if (instance) {
            instance.dispose();
        }

        element.removeAttribute("data-job-tooltip");
    });

    document.querySelectorAll("[data-bs-toggle='tooltip']").forEach((element) => {
        element.setAttribute("data-job-tooltip", "true");
        new bootstrap.Tooltip(element);
    });
};

document.addEventListener("DOMContentLoaded", () => {
    window.jobScheduler.refreshTooltips();
});

document.addEventListener("enhancedload", () => {
    window.jobScheduler.refreshTooltips();
});
