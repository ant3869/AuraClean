using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AuraClean.ViewModels;

public partial class OnboardingViewModel : ObservableObject
{
    public event Action? OnboardingCompleted;

    [ObservableProperty] private int _currentStep;
    [ObservableProperty] private int _totalSteps = 4;
    [ObservableProperty] private bool _isVisible;

    // Step indicator states
    [ObservableProperty] private bool _isStep0;
    [ObservableProperty] private bool _isStep1;
    [ObservableProperty] private bool _isStep2;
    [ObservableProperty] private bool _isStep3;

    // System facts shown during onboarding
    [ObservableProperty] private string _machineName = Environment.MachineName;
    [ObservableProperty] private string _userName = Environment.UserName;

    public OnboardingViewModel()
    {
        CurrentStep = 0;
        UpdateStepFlags();
    }

    public bool ShouldShowOnboarding()
    {
        var settings = SettingsService.Load();
        return !settings.HasCompletedOnboarding;
    }

    public void Show()
    {
        CurrentStep = 0;
        UpdateStepFlags();
        IsVisible = true;
    }

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep < TotalSteps - 1)
        {
            CurrentStep++;
            UpdateStepFlags();
        }
        else
        {
            CompleteOnboarding();
        }
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep > 0)
        {
            CurrentStep--;
            UpdateStepFlags();
        }
    }

    [RelayCommand]
    private void SkipOnboarding()
    {
        CompleteOnboarding();
    }

    [RelayCommand]
    private void GoToStep(int step)
    {
        if (step >= 0 && step < TotalSteps)
        {
            CurrentStep = step;
            UpdateStepFlags();
        }
    }

    private void CompleteOnboarding()
    {
        var settings = SettingsService.Load();
        settings.HasCompletedOnboarding = true;
        SettingsService.Save(settings);

        IsVisible = false;
        OnboardingCompleted?.Invoke();
    }

    private void UpdateStepFlags()
    {
        IsStep0 = CurrentStep == 0;
        IsStep1 = CurrentStep == 1;
        IsStep2 = CurrentStep == 2;
        IsStep3 = CurrentStep == 3;
    }
}
