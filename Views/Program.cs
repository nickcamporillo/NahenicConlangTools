﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Configuration;
using System.Data;
using System.Windows.Forms;
using System.Diagnostics;

using log4net.Appender;
using log4net.Config;
using log4net;
using log4net.Repository.Hierarchy;

using Lexicon.Legacy2019.IPresenters;
using Lexicon.Legacy2019.Presenters;
using Lexicon.Legacy2019.IViews;
using Lexicon.Legacy2019.Services;
using Lexicon.Legacy2019.IRepository;
using Lexicon.Legacy2019.Repositories;

namespace Lexicon.Legacy2019.Screens
{
    static class Program
    {
        static EventHandler Error;
        static EventHandler Activate;
        static EventHandler Deactivate;

        enum NavigationDirection
        {
            Forward = 1,
            Backward = 2
        }

        #region "Local Constants"
        //Configuration name in app.config
        private const string APP_VERSION_CONFIG = "APPLICATION_VERSION";
        private const string CONFIG_CNX_STRING = "Lexicon2019_TransitionalEntities";
        //private const string CONFIG_CNX_STRING = "CNX";
        private const string LOG_DIR = "LOG_DIR";

        //Exception message strings
        private const string ERR_GENERAL = "Please contact system administrator.";
        private const string ERR_GENERAL_CAPTION = "Issue initializing Lexicon application";
        //private const string ERR_INCORRECT_NUM_ARGS = "Number of parameters incorrect";
        //private const string ERR_INCORRECT_FORMAT_ARGS = "Parameter format incorrect";

        //User-input-related stuff
        private const char KEYPRESS_ENTER = '\r';
        private const char KEYPRESS_TAB = '\t';

        //Form-related stuff
        private static IList<IView> forms;

        private const string LEXICON_ENTRY_SCREEN = "LexiconEntryScreen";
        private const string WORD_LIST_SCREEN = "WordListGrid";

        private const int STARTING_FORM = 0;
        private const int STANDARD_FORM_SIZE = 321;

        #endregion


        //https://stackify.com/log4net-guide-dotnet-logging/
        //Recipe for simplified configuration by using app.config: https://www.codeproject.com/Tips/1107824/Perfect-Log-Net-with-Csharp
        private static readonly ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] commandLine)
        {
            SetLogLocation(commandLine, "LegacyLexicon");

            log.Info($"== Begin Logging [{DateTime.Now.ToLocalTime()}] ==");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Error += Application_Error;
            Activate += Application_Activate;
            Deactivate += Application_Deactivate;

            RunProgram(commandLine);
        }

        private static void RunProgram(string[] commandLine)
        {
            BaseService service = null;
            string cnxString = string.Empty;
            StartupMessageWindow messager = new StartupMessageWindow();

            try
            {
                ConnectionStringSettingsCollection cnxStrings = ConfigurationManager.ConnectionStrings;
                cnxString = cnxStrings[CONFIG_CNX_STRING].ConnectionString;

                IUnitOfWork uow = new EfUnitOfWork();

                service = new DumbService(uow);

                //service.Activate();

                forms = new List<IView>();                
                forms.Add(CreateForm(typeof(WordListGrid), service));
                forms.Add(CreateForm(typeof(LexiconEntryScreen), service));

                Application.Run(forms[STARTING_FORM] as Form);
            }
            catch (Exception ex)
            {
                log.Error($"Exception: {ex.Message}");

                if (service != null)
                {
                    service.Dispose();
                    service = null;
                }

                messager.Message(ERR_GENERAL_CAPTION, ERR_GENERAL, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                messager.Dispose();
            }
        }

        //This is the Form Factory method
        private static IView CreateForm(Type typ, BaseService service)
        {
            IView form = null;
            IPresenter pres = null;

            string versionNumber =  ConfigurationManager.AppSettings[APP_VERSION_CONFIG].ToString();
            string prefix = $"Lexicon Manager 2019 {versionNumber} - ";
            string suffix = string.Empty;

            if (typ == typeof(WordListGrid))
            {
                suffix = "Word List Grid";
                pres = new WordListPresenter(service);
                form = new WordListGrid(pres);
                form.Name = LEXICON_ENTRY_SCREEN;
                form.PreviousFormName = string.Empty;
                form.NextFormName = WORD_LIST_SCREEN;

                //Set "false" for the Previous screen, bc this is the first screen (there's no previous screen to move to.
                //Since this is read-only screen, it should always have permission to move forward.
                form.ConfirmNavigateToPreviousScreen = ConfirmNavigation.StayOnCurrentScreen;
                form.ConfirmNavigateToNextScreen = ConfirmNavigation.MoveToNextScreen;
                form.CloseAll += Form_CloseAll;
            }
            else if (typ == typeof(LexiconEntryScreen))
            {
                suffix = "Data Entry Screen";
                pres = new LexiconEntryPresenter(service);
                form = new LexiconEntryScreen(pres);
                form.Name = WORD_LIST_SCREEN;
                form.PreviousFormName = LEXICON_ENTRY_SCREEN;
                form.NextFormName = LEXICON_ENTRY_SCREEN;

                //This is also a read-only screen.  Since it's the middle screen, it should always have unconditional permission to move forward and backwards.
                form.ConfirmNavigateToPreviousScreen = ConfirmNavigation.MoveToNextScreen;
                form.ConfirmNavigateToNextScreen = ConfirmNavigation.MoveToNextScreen;

                form.CloseAll += Form_CloseAll;
            }

            form.Text = prefix + suffix;
            if (form.GetType() == typeof(Form))
            {
                //Can't do the following - access is private:
                //((Form)(form)).CenterToScreen();

                //...and this executes too late, so you have to also do this inside the Form:
                //this.WindowState = FormWindowState.Maximized;

                //...however, these props are accessible here and occur at real-time
                ((Form)(form)).MaximizeBox = false;
                ((Form)(form)).MinimizeBox = true;
            }

            form.NextScreen += Form_NextScreen;
            form.PreviousScreen += Form_PreviousScreen;
            ((Form)(form)).FormClosing += Program_FormClosing;

            return form;
        }
        
        private static void Form_CloseAll(object sender, EventArgs e)
        {
            foreach (var f in forms)
            {
                f.Close();
            }
            forms.Clear();
            forms = null;
        }

        //Invoke any cleanup if necessary
        private static void Program_FormClosing(object sender, FormClosingEventArgs e)
        {
            IView sendingView = GetSendingForm(sender);

            if (sendingView != null)
            {
                string name = sendingView.Name;
                foreach (var form in forms)
                {
                    form.Dispose();
                }
            }
        }

        #region "Navigation Methods and Related Funcs"
        private static void Form_PreviousScreen(object sender, EventArgs e)
        {
            IView currentForm = null;
            IView nextForm = null;

            currentForm = GetSendingForm(sender);

            if (currentForm != null && currentForm.ConfirmNavigateToPreviousScreen == ConfirmNavigation.MoveToNextScreen)
            {
                if (!string.IsNullOrEmpty(currentForm.PreviousFormName))
                {
                    nextForm = forms.Where(c => c.Name.Equals(currentForm.PreviousFormName)).FirstOrDefault();
                    if (nextForm != null)
                    {
                        SetFormAttributes(ref nextForm, currentForm, NavigationDirection.Backward);

                        if (currentForm is IViewWordListGrid)
                        {
                            currentForm.ConfirmNavigateToPreviousScreen = ConfirmNavigation.MoveToNextScreen;
                        }

                        nextForm.Show();
                    }
                }

                currentForm.Hide();
            }
        }
        private static void Form_NextScreen(object sender, EventArgs e)
        {
            IView currentForm = null;
            IView nextForm = null;

            currentForm = GetSendingForm(sender);

            if (currentForm != null)
            {
                if (!string.IsNullOrEmpty(currentForm.NextFormName) && currentForm.ConfirmNavigateToNextScreen == ConfirmNavigation.MoveToNextScreen)
                {
                    nextForm = forms.Where(c => c.Name.Equals(currentForm.NextFormName)).FirstOrDefault();
                    if (nextForm != null)
                    {
                        SetFormAttributes(ref nextForm, currentForm, NavigationDirection.Forward);
                        nextForm.Show();
                    }
                }

                currentForm.Hide();
            }
        }

        private static IView GetSendingForm(object sender)
        {
            Control ctrl;
            IView sendingForm = sender as IView;

            if (sendingForm != null)
            {
                return sendingForm;
            }

            ctrl = sender as Control;            

            sendingForm = GetParentRecursively(ctrl);

            return sendingForm;
        }

        //I created this bc sometimes controls are contained in more controls.  This walks up the container ladder
        private static IView GetParentRecursively(Control ctrl)
        {   
            IView view = ctrl.Parent as IView;

            if (view != null)
            {
                return ctrl.Parent as IView;
            }
            else
            {
                return GetParentRecursively(ctrl.Parent.Parent);
            }
        }

        private static void SetFormAttributes(ref IView view, IView sendingForm, NavigationDirection direction)
        {
            string sendingFormName = sendingForm.Name;
            string targetViewName = view.Name;

            //Suppose that IViewContact is in Add New Building mode.  The PlaceState is going to be empty...so when you return from IViewContact back to IViewBuilder, you don't want to
            //erase the value in IViewBuilder's PlaceState field...that's supposed to be based only on the value from the PlaceState field value of the preceding form, IViewBPOID.
            if (!(sendingForm is IViewLexiconEntryScreen))
            {
                //view.Psu = sendingForm.Psu;
                //view.Bpoid = sendingForm.Bpoid;
                //view.Place = sendingForm.Place;
                //view.PlaceState = sendingForm.PlaceState;
            }

            if (view is IViewWordListGrid)
            {
                //if (view is IViewContact && ((IViewContact)(view)).EntryMode != BuilderEntryMode.AddBuilderAndContact)
                //{
                //    ((IViewBuilderCode)(view)).BuilderCode = Utilities.GetPropertyValue(sendingForm, "BuilderCode");
                //}
            }

            if (direction == NavigationDirection.Forward)
            {
                if (sendingFormName.Equals(LEXICON_ENTRY_SCREEN))
                {
                    //No attributes needed to set
                }
                else if (sendingFormName.Equals(WORD_LIST_SCREEN))
                {

                }
            }
        }

        #endregion

        #region Log4Net stuff"

        public class ActivityIdHelper
        {
            public override string ToString()
            {
                if (Trace.CorrelationManager.ActivityId == Guid.Empty)
                {
                    Trace.CorrelationManager.ActivityId = Guid.NewGuid();
                }

                return Trace.CorrelationManager.ActivityId.ToString();
            }
        }

        static void Application_Activate(object sender, EventArgs e)
        {
            //set the property to our new object
            log4net.LogicalThreadContext.Properties["activityid"] = new ActivityIdHelper();

            log.Debug("Application_Activate");
        }

        static void Application_Deactivate(object sender, EventArgs e)
        {
            //set the property to our new object
            log4net.LogicalThreadContext.Properties["activityid"] = new ActivityIdHelper();
            log.Debug("Application_Deactivate");
        }

        static void Application_Error(object sender, EventArgs e)
        {
            //set the property to our new object
            log4net.LogicalThreadContext.Properties["activityid"] = new ActivityIdHelper();
            log.Debug("Application_Error");
        }

        //Programatically set the log file's location dynamically to database's containing folder; how-to located here: https://stackoverflow.com/questions/17560396/log4net-how-to-set-logger-file-name-dynamically
        private static void SetLogLocation(string[] commandLine, string fileName)
        {
            string logPath = $@"{ConfigurationManager.AppSettings[LOG_DIR].ToString()}\{fileName}_{DateTime.Now.Year}{DateTime.Now.Month}{DateTime.Now.Day}_{DateTime.Now.Hour}{DateTime.Now.Minute}.log";

            XmlConfigurator.Configure();
            Hierarchy heirarchy = (Hierarchy)LogManager.GetRepository();

            foreach (IAppender appender in heirarchy.Root.Appenders)
            {
                if (appender is FileAppender)
                {
                    FileAppender fa = (FileAppender)appender;
                    fa.File = logPath;
                    fa.ActivateOptions();
                    break;
                }
            }
        }

        #endregion
    }


}
