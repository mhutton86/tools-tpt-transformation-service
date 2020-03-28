﻿// Get job ID & project name from script args
var jobId = app.scriptArgs.getValue("jobId");
var projectName = app.scriptArgs.getValue("projectName");
var bookFormat = app.scriptArgs.getValue("bookFormat");

// Set top-level base and output dirs
var idttDir = 'C:\\Work\\IDTT\\';
var idmlDir = 'C:\\Work\\IDML\\';
var pdfDir = 'C:\\Work\\PDF\\';

// Set project input dir and output file
var txtDir = idttDir + bookFormat + '\\' + projectName + '\\';
var bookPath = idmlDir + 'preview-' + jobId + '.indb';
var pdfPath = pdfDir + 'preview-' + jobId + '.pdf';

// Create book to aggreage individual docs
var book = app.books.add(bookPath);
book.automaticPagination = true;

// Find & sort source documents to read
var txtFolder = new Folder(txtDir);
var txtFiles1 = txtFolder.getFiles("books-*.txt");
txtFiles1.sort(function (f1, f2) {
    return f1.name.localeCompare(f2.name);
});
var txtFiles2 = txtFolder.getFiles("book-*.txt");
txtFiles2.sort(function (f1, f2) {
    return f1.name.localeCompare(f2.name);
});
var txtFiles = txtFiles1.concat(txtFiles2);

// Create IDS document for each source, then add to book
for (var ctr = 0;
    ctr < txtFiles.length;
    ctr++) {
    var txtFile = txtFiles[ctr];
    var docPath = idmlDir + 'preview-' + jobId + '-' + txtFile.name.replace('.txt', '') + '.indd';

    // Load template as starting point
    var doc = app.open(idmlDir + 'preview-' + jobId + '.idml');
    doc.preflightOptions.preflightOff = true;

    // Setup doc, composer, etc. for Arabic
    for (var i = 1; i < doc.paragraphStyles.count(); i++) {
        if (doc.paragraphStyles[i].basedOn === "[No Paragraph Style]") {
            doc.paragraphStyles[i].composer = "Adobe World-Ready Paragraph Composer";
            doc.paragraphStyles[i].appliedLanguage = app.languagesWithVendors.itemByName("Arabic");
            doc.paragraphStyles[i].appliedFont = "Adobe Arabic";
            doc.paragraphStyles[i].paragraphDirection = ParagraphDirectionOptions.RIGHT_TO_LEFT_DIRECTION;
        }
        if (doc.paragraphStyles[i].name === "DefaultHeading") {
            doc.paragraphStyles[i].composer = "Adobe World-Ready Paragraph Composer";
            doc.paragraphStyles[i].appliedLanguage = app.languagesWithVendors.itemByName("Arabic");
            doc.paragraphStyles[i].appliedFont = "Adobe Arabic";
            doc.paragraphStyles[i].paragraphDirection = ParagraphDirectionOptions.RIGHT_TO_LEFT_DIRECTION;
        }
    }
    for (var i = 1; i < doc.characterStyles.count(); i++) {
        if (doc.characterStyles[i].appliedFont === "Myriad Pro") {
            doc.characterStyles[i].appliedFont = "Adobe Arabic";
            doc.characterStyles[i].appliedLanguage = app.languagesWithVendors.itemByName("Arabic");
        }
    }

    try {

        // Place text
        var layer = doc.layers.lastItem();
        var pageItem = layer.pageItems.lastItem();
        var placement = pageItem.place(txtFile);
        var story = placement[0];

        // Identify starting points for new pages
        var spread = doc.spreads[1];
        var masterPages = doc.masterSpreads[0].pages;
        var masterLeftTextFrame = masterPages[0].textFrames.firstItem();
        var masterRightTextFrame = masterPages[1].textFrames.firstItem();
        var lastTextFrame = doc.pages.lastItem().textFrames.lastItem();

        // Add spreads and connect to provide the right amount of space for text
        while (lastTextFrame.contents.length > 0) {
            var priorLastTF = lastTextFrame.previousTextFrame;

            priorLastTF.nextTextFrame = null;
            lastTextFrame.previousTextFrame = null;

            var newSpread = doc.spreads.add(LocationOptions.AFTER, spread);
            var newLeftTf = masterLeftTextFrame.duplicate(newSpread.pages[0]);
            var newRightTf = masterRightTextFrame.duplicate(newSpread.pages[1]);

            priorLastTF.nextTextFrame = newLeftTf;
            newLeftTf.nextTextFrame = newRightTf;
            lastTextFrame.previousTextFrame = newRightTf;

            spread = newSpread;
        }

        for (var p = doc.pages.length - 1; p > 0; p--) {
            if (doc.pages[p].textFrames[0].contents.length == 0) {
                doc.pages[p].remove();
            }
        }
        doc.preflightOptions.preflightOff = false;

        // Save INDD file
        doc.save(docPath);
        doc.close(SaveOptions.YES);

        // Add to book
        book.bookContents.add(docPath);
        book.save()
    } catch (ex) {
        alert("Can't create document: " + docPath
            + ", project: " + projectName
            + ", format: " + bookFormat
            + ", cause: " + ex);
    }
}

// Save book & export to PDF
book.save();
book.exportFile(ExportFormat.PDF_TYPE, pdfPath);

// Close & exit
book.close(SaveOptions.YES);
