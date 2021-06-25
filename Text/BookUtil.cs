﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Reflection;
using CsvHelper;
using CsvHelper.Configuration;
using static System.String;

namespace TptMain.Text
{
    /// <summary>
    /// Bible text-related utilities.
    /// </summary>
    public class BookUtil
    {
        /// <summary>
        /// Book id list, from resource file.
        /// </summary>
        public static readonly IList<BookIdItem> BookIdList;

        /// <summary>
        /// Map of book codes to IDs.
        /// </summary>
        public static readonly IDictionary<string, BookIdItem> BookIdsByCode;

        /// <summary>
        /// Map of book numbers (1-based) to IDs.
        /// </summary>
        public static readonly IDictionary<int, BookIdItem> BookIdsByNum;

        static BookUtil()
        {
            var executingAssembly = Assembly.GetExecutingAssembly();
            using var streamReader = new StreamReader(executingAssembly.GetManifestResourceStream("TptMain.Resources.book-ids-1.csv")!);
            using var csvReader = new CsvReader(streamReader, CultureInfo.CurrentCulture);

            csvReader.Configuration.HasHeaderRecord = false;
            csvReader.Configuration.IgnoreBlankLines = true;
            csvReader.Configuration.TrimOptions = TrimOptions.Trim;
            csvReader.Configuration.MissingFieldFound = null;

            BookIdList = csvReader.GetRecords<BookIdItem>().ToImmutableList();
            BookIdsByCode = BookIdList.ToImmutableDictionary(idItem => $"{idItem.UsfmBookNum}{idItem.BookCode}");
            BookIdsByNum = BookIdList.ToImmutableDictionary(idItem => idItem.BookNum);
        }
    }
}
