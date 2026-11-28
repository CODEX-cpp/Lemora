# Text Library

## How to Add or Edit a Language

Text resources for multilingual support can be found in `Ui/Resources/Languages`.

Each language has its own CSV file in the `glossary/` directory. The `*.xaml` and `*.cs` files are generated from these CSV files and should not be edited directly.

### To Add a New Language

1. **Copy the English template**: Make a copy of `glossary/en-us.csv` and rename it to the appropriate language code (e.g., `es-es.csv` for Spanish, `ko-kr.csv` for Korean).

2. **Edit the language metadata**: Open the new CSV file and update the first 4 rows:
   - Row 1: Change `en-us` to your language code
   - Row 2: Set the ISO language code (e.g., `es-ES`, `ko-KR`)
   - Row 3: Set the Google Translate language code (e.g., `es`, `ko`)
   - Row 4: Set the display name (e.g., `Español (es-ES)`, `한국어 (ko-KR)`)

3. **Translate the content**: Edit the translations in the second column. You can use any text editor, but be careful not to change the encoding. Avoid using Office Excel. You can use https://edit-csv.net to edit it.

4. **Generate XAML files**: Run `conver_glossary_to_xaml.py` to generate the `*.xaml` and `LanguageList.cs` files.

### To Edit an Existing Language

Simply edit the corresponding CSV file in the `glossary/` directory and run `conver_glossary_to_xaml.py`.

> [!TIP]
> When editing CSV files, note the following:
>
> - The delimiter is a semicolon `;`
> - The line ending character is LF
> - To include semicolons or line breaks in text, enclose the entire text in double quotation marks
> - To include double quotation marks in text, enclose the entire text in double quotation marks, where the double quotation marks are represented by `""`
>
> Terms left blank in the CSV files will be automatically filled using Google Translate if available, and cached in `glossary_translated_by_google/` directory.

> [!NOTE]
> `conver_glossary_to_xaml.py` requires Python support.
> The first run may fail, so try it again.
>
> To access Google Translate, you may need to edit `conver_glossary_to_xaml.py`.
> For example, to remove proxy:
>
> ```python
>  # http_proxy = SyncHTTPProxy((b'http', b'127.0.0.1', 1080, b''))
>  # proxies = {'http': http_proxy, 'https': http_proxy}
>  # translator = Translator(proxies=proxies)
>  translator = Translator()
> ```

## How to Add or Edit a Language (short version)

### Add a language

1. **Copy the English template**: Make a copy of `glossary/en-us.csv` and rename it to the code of the language (e.g. `es-es.csv` for Spanish, `ko-kr.csv` for Korean).

2. **Edit the language info**: Open the new CSV file and change the language info in the first 4 rows:
   - Row 1: change `en-us` to your language code
   - Row 2: set the ISO language code (e.g. `es-ES`, `ko-KR`)
   - Row 3: set the Google Translate language code (e.g. `es`, `ko`)
   - Row 4: set the display name (e.g. `Español (es-ES)`, `한국어 (ko-KR)`)

3. **Translate the content**: Edit the translated text in the second column. Do not open the CSV with Office Excel, because Excel changes the file encoding. You can use <https://edit-csv.net> to edit it.

4. **Generate XAML files**: Run `conver_glossary_to_xaml.py` to import the translated text into the app source code.

### Edit an existing language

Edit the matching CSV file in the `glossary/` directory, then run `conver_glossary_to_xaml.py`.

> [!TIP]
> Terms left blank in the CSV files are filled automatically by Google Translate and cached in the `glossary_translated_by_google/` directory.

> [!NOTE]
> `conver_glossary_to_xaml.py` requires a Python environment.
